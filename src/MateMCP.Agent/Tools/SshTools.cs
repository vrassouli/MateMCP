using System.ComponentModel;
using System.Net;
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
public sealed class SshTools(
    ProjectRegistry projects,
    AuditLog audit,
    IApprovalService approvals,
    IOptions<MateOptions> options,
    InteractiveShellSessionManager sessions,
    AgentActivityGate? activity = null)
{
    private readonly AgentActivityGate _activity = activity ?? new AgentActivityGate();

    [McpServerTool(
        Name = "ssh_session_start",
        Title = "Start SSH session",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = true)]
    [Description("Starts an interactive SSH client session using structured host, username, and port parameters. This avoids passing an arbitrary shell command to shell_session_start. Continue the session with shell_session_read, shell_session_write, shell_session_send_secret, and shell_session_close.")]
    public async Task<object> Start(
        [Description("SSH server hostname or IP address.")] string host,
        [Description("SSH username. Letters, digits, dot, underscore, and hyphen are supported.")] string username,
        [Description("SSH TCP port. Defaults to 22.")] int port = 22,
        [Description("Optional configured MateMCP project whose working directory should host the local SSH client process.")] string? project = null,
        CancellationToken cancellationToken = default)
    {
        using var activityLease = EnterActivity();
        var command = SshSessionCommand.Build(host, username, port);
        var (workingDirectory, scope) = ResolveWorkingDirectory(project);
        var destination = $"{username}@{host}:{port}";

        if (options.Value.RequireShellApproval)
        {
            var decision = await approvals.RequestAsync("shell.exec", scope, $"Open interactive SSH connection to {destination}", cancellationToken);
            if (decision == ApprovalDecision.Deny)
            {
                await audit.WriteAsync("shell.ssh.start", $"{scope}:{destination}", "denied:approval", cancellationToken);
                throw new McpException("Interactive SSH connection denied by local user.");
            }
            if (decision == ApprovalDecision.Timeout)
            {
                await audit.WriteAsync("shell.ssh.start", $"{scope}:{destination}", "denied:approval-timeout", cancellationToken);
                throw new McpException("Interactive SSH connection approval timed out.");
            }
        }

        try
        {
            var result = await sessions.StartAsync(command, workingDirectory, cancellationToken);
            await audit.WriteAsync("shell.ssh.start", $"{scope}:{destination}", $"started:{result.SessionId}", cancellationToken);
            return result;
        }
        catch (Exception ex) when (ex is not McpException)
        {
            await audit.WriteAsync("shell.ssh.start", $"{scope}:{destination}", $"failed:{ex.GetType().Name}", CancellationToken.None);
            throw new McpException($"Could not start interactive SSH session: {ex.Message}");
        }
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
}

public static class SshSessionCommand
{
    public static string Build(string host, string username, int port = 22)
    {
        host = (host ?? string.Empty).Trim();
        username = (username ?? string.Empty).Trim();

        if (port is < 1 or > 65_535)
            throw new ArgumentOutOfRangeException(nameof(port), "SSH port must be between 1 and 65535.");
        if (!IsValidHost(host))
            throw new ArgumentException("SSH host must be a valid hostname or IP address.", nameof(host));
        if (!IsValidUsername(username))
            throw new ArgumentException("SSH username may contain only letters, digits, dot, underscore, and hyphen.", nameof(username));

        return $"ssh -p {port} {username}@{host}";
    }

    private static bool IsValidHost(string host)
    {
        if (host.Length is < 1 or > 253 || host[0] == '-') return false;
        if (IPAddress.TryParse(host, out _)) return true;
        return Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    private static bool IsValidUsername(string username)
    {
        if (username.Length is < 1 or > 128 || username[0] == '-') return false;
        return username.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    }
}
