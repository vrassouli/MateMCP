using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Projects;
using MateMCP.Agent.Security;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class InteractiveShellTools(
    ProjectRegistry projects,
    AuditLog audit,
    IApprovalService approvals,
    IOptions<MateOptions> options,
    InteractiveShellSessionManager sessions,
    ICredentialStore secrets)
{
    [McpServerTool(Name = "shell_session_start"), Description("Starts an interactive shell command in a real PTY/ConPTY and returns a session id plus initial terminal output. Use shell_session_read to observe later output and shell_session_write or shell_session_send_secret to respond to prompts.")]
    public async Task<object> Start(string command, string? project = null, CancellationToken cancellationToken = default)
    {
        var (workingDirectory, scope) = ResolveWorkingDirectory(project);
        if (options.Value.RequireShellApproval)
        {
            var decision = await approvals.RequestAsync("shell.exec", scope, Trim(command), cancellationToken);
            if (decision == ApprovalDecision.Deny)
            {
                await audit.WriteAsync("shell.session.start", $"{scope}:{Trim(command)}", "denied:approval", cancellationToken);
                throw new McpException("Interactive shell execution denied by local user.");
            }
            if (decision == ApprovalDecision.Timeout)
            {
                await audit.WriteAsync("shell.session.start", $"{scope}:{Trim(command)}", "denied:approval-timeout", cancellationToken);
                throw new McpException("Interactive shell execution approval timed out.");
            }
        }

        try
        {
            var result = await sessions.StartAsync(command, workingDirectory, cancellationToken);
            await audit.WriteAsync("shell.session.start", $"{scope}:{Trim(command)}", $"started:{result.SessionId}", cancellationToken);
            return result;
        }
        catch (Exception ex) when (ex is not McpException)
        {
            await audit.WriteAsync("shell.session.start", $"{scope}:{Trim(command)}", $"failed:{ex.GetType().Name}", CancellationToken.None);
            throw new McpException($"Could not start interactive shell session: {ex.Message}");
        }
    }

    [McpServerTool(Name = "shell_session_read"), Description("Reads terminal output produced by an interactive shell session since the supplied offset. Pass nextOffset from the previous response to receive only new output. outputTruncated indicates that older buffered output was discarded.")]
    public object Read(string sessionId, int offset = 0)
    {
        try { return sessions.Read(sessionId, offset); }
        catch (KeyNotFoundException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "shell_session_write"), Description("Writes ordinary non-secret text to an existing interactive shell session as terminal input. Set submit=true to press Enter after the text.")]
    public async Task<object> Write(string sessionId, string text, bool submit = true, CancellationToken cancellationToken = default)
    {
        try
        {
            await sessions.WriteAsync(sessionId, text, submit, cancellationToken);
            await audit.WriteAsync("shell.session.write", sessionId, $"chars:{text.Length};submit:{submit}", cancellationToken);
            return new { sessionId, written = true };
        }
        catch (KeyNotFoundException ex) { throw new McpException(ex.Message); }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "shell_session_send_secret"), Description("Injects a locally stored named credential into an existing interactive shell session without revealing the secret value to the AI. Use this only after observing terminal output and deciding that this exact running process is requesting that credential. Set submit=true to press Enter after the secret.")]
    public async Task<object> SendSecret(string sessionId, string credential, bool submit = true, CancellationToken cancellationToken = default)
    {
        string command;
        try { command = sessions.GetCommand(sessionId); }
        catch (KeyNotFoundException ex) { throw new McpException(ex.Message); }

        var available = await secrets.ListAsync(cancellationToken);
        var info = available.FirstOrDefault(x => string.Equals(x.Name, credential, StringComparison.OrdinalIgnoreCase));
        if (info is null) throw new McpException($"Named credential '{credential}' does not exist.");

        var commandFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command))).ToLowerInvariant()[..16];
        var approvalTarget = $"{info.Name}@cmd:{commandFingerprint}";
        var decision = await approvals.RequestAsync("secret.use", approvalTarget, $"Use credential {info.Name} in shell session {sessionId[..Math.Min(8, sessionId.Length)]}: {Trim(command)}", cancellationToken);
        if (decision == ApprovalDecision.Deny)
        {
            await audit.WriteAsync("secret.use", $"{info.Name}:{sessionId}", "denied:approval", cancellationToken);
            throw new McpException("Credential use denied by local user.");
        }
        if (decision == ApprovalDecision.Timeout)
        {
            await audit.WriteAsync("secret.use", $"{info.Name}:{sessionId}", "denied:approval-timeout", cancellationToken);
            throw new McpException("Credential use approval timed out.");
        }

        var value = await secrets.ResolveAsync(info.Name, cancellationToken);
        if (value is null) throw new McpException($"Named credential '{info.Name}' could not be resolved from the local secure store.");
        try
        {
            await sessions.WriteSecretAsync(sessionId, value, submit, cancellationToken);
            await audit.WriteAsync("secret.use", $"{info.Name}:{sessionId}", $"injected:cmd:{commandFingerprint}", cancellationToken);
            return new { sessionId, credential = info.Name, injected = true, submit };
        }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }
        finally { value = null; }
    }

    [McpServerTool(Name = "shell_session_close"), Description("Terminates and removes an interactive shell session.")]
    public async Task<object> Close(string sessionId, CancellationToken cancellationToken = default)
    {
        var closed = sessions.Close(sessionId);
        await audit.WriteAsync("shell.session.close", sessionId, closed ? "closed" : "not-found", cancellationToken);
        return new { sessionId, closed };
    }

    [McpServerTool(Name = "secret_list"), Description("Lists locally configured credential names, types, and descriptions. Secret values are never returned.")]
    public async Task<object> ListSecrets(CancellationToken cancellationToken = default)
    {
        var list = await secrets.ListAsync(cancellationToken);
        return list.Select(x => new { x.Name, type = x.Kind.ToString(), x.Description }).ToArray();
    }

    private (string WorkingDirectory, string Scope) ResolveWorkingDirectory(string? project)
    {
        if (!string.IsNullOrWhiteSpace(project))
        {
            var definition = projects.Get(project);
            if (!definition.Shell) throw new McpException($"Shell access is disabled for project '{project}'.");
            return (definition.Root, $"project:{project}");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var directory = !string.IsNullOrWhiteSpace(home) && Directory.Exists(home) ? home : Environment.CurrentDirectory;
        return (directory, "agent");
    }

    private static string Trim(string value) => value.Length <= 500 ? value : value[..500] + "…";
}
