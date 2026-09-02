using System.ComponentModel;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Desktop;
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
    ICredentialStore secrets,
    CredentialInjectionRateLimiter injectionRateLimiter,
    AgentActivityGate? activity = null)
{
    private const string SendSecretTool = UserSecretInfo.ShellSessionSendSecretTool;
    private readonly AgentActivityGate _activity = activity ?? new AgentActivityGate();

    [McpServerTool(
        Name = "shell_session_start",
        Title = "Start interactive shell session",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true)]
    [Description("Starts any shell/command-line command in a real PTY/ConPTY and returns a session id plus initial terminal output. Use this instead of shell_exec whenever the command may prompt, wait for terminal input, open an interactive program, or require a credential. Continue with shell_session_read, shell_session_write, shell_session_send_secret, and shell_session_close.")]
    public async Task<object> Start(
        [Description("Shell/command-line command to run. This is intentionally generic and may be any command supported by the local shell.")] string command,
        [Description("Optional configured MateMCP project whose directory and shell policy should be used. Omit to run from the Agent user's home directory.")] string? project = null,
        CancellationToken cancellationToken = default)
    {
        using var activityLease = EnterActivity();
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

    [McpServerTool(
        Name = "shell_session_read",
        Title = "Read interactive shell output",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Reads terminal output produced by an interactive shell session since the supplied offset. Call this after starting a session and after each write or secret injection to observe prompts, progress, completion, or further input requests. Pass nextOffset from the previous response to receive only new output.")]
    public object Read(
        [Description("Session id returned by shell_session_start.")] string sessionId,
        [Description("Absolute output offset. Use nextOffset from the previous response; use 0 for the first read.")] int offset = 0)
    {
        try { return sessions.Read(sessionId, offset); }
        catch (KeyNotFoundException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(
        Name = "shell_session_write",
        Title = "Write interactive shell input",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = true)]
    [Description("Writes ordinary non-secret text to an existing interactive shell session. Use it for confirmations, menu choices, commands, REPL input, and other visible terminal input. Never place a password/token/secret value in this tool; use shell_session_send_secret with a credential name instead.")]
    public async Task<object> Write(
        [Description("Session id returned by shell_session_start.")] string sessionId,
        [Description("Ordinary non-secret terminal input.")] string text,
        [Description("Whether to press Enter after the text.")] bool submit = true,
        CancellationToken cancellationToken = default)
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

    [McpServerTool(
        Name = "shell_session_send_secret",
        Title = "Use stored secret in shell session",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true)]
    [Description("Injects a locally stored named credential into an existing interactive shell session without revealing the credential value to the AI. Use this only after shell_session_read shows that the exact running process is requesting that credential. Pass only the credential name/reference, never its secret value.")]
    public Task<object> SendSecret(
        [Description("Session id returned by shell_session_start.")] string sessionId,
        [Description("Name/reference of a locally stored credential. The credential value remains inside MateMCP Agent.")] string credential,
        [Description("Whether to press Enter after injecting the credential.")] bool submit = true,
        CancellationToken cancellationToken = default)
        => ShellSecretInjector.InjectAsync(sessionId, credential, submit, SendSecretTool, sessions, secrets,
            injectionRateLimiter, approvals, audit, cancellationToken);

    [McpServerTool(
        Name = "shell_session_close",
        Title = "Close interactive shell session",
        ReadOnly = false,
        Destructive = true,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Terminates and removes an interactive shell session. Use this when the command is complete or the session is no longer needed.")]
    public async Task<object> Close(
        [Description("Session id returned by shell_session_start.")] string sessionId,
        CancellationToken cancellationToken = default)
    {
        var closed = sessions.Close(sessionId);
        await audit.WriteAsync("shell.session.close", sessionId, closed ? "closed" : "not-found", cancellationToken);
        return new { sessionId, closed };
    }

    [McpServerTool(
        Name = "secret_list",
        Title = "List stored credential references",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false)]
    [Description("Lists locally configured credential names, types, descriptions, and allowed tool metadata. Secret values are never returned.")]
    public async Task<object> ListSecrets(CancellationToken cancellationToken = default)
    {
        var list = await secrets.ListAsync(cancellationToken);
        return list.Select(x => new { x.Name, type = x.Kind.ToString(), x.Description, allowedTools = x.EffectiveAllowedTools }).ToArray();
    }

    private IDisposable EnterActivity()
    {
        if (!_activity.TryEnter(out var lease) || lease is null)
            throw new McpException("MateMCP Agent is preparing a verified Desktop update. Retry after the Agent restarts.");
        return lease;
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
