using System.Security.Cryptography;
using System.Text;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Security;
using ModelContextProtocol;

namespace MateMCP.Agent.Tools;

internal static class ShellSecretInjector
{
    public static async Task<object> InjectAsync(
        string sessionId,
        string credential,
        bool submit,
        string toolName,
        InteractiveShellSessionManager sessions,
        ICredentialStore secrets,
        CredentialInjectionRateLimiter injectionRateLimiter,
        IApprovalService approvals,
        AuditLog audit,
        CancellationToken cancellationToken)
    {
        string command;
        try { command = sessions.GetCommand(sessionId); }
        catch (KeyNotFoundException ex) { throw new McpException(ex.Message); }

        var available = await secrets.ListAsync(cancellationToken);
        var info = available.FirstOrDefault(x => string.Equals(x.Name, credential, StringComparison.OrdinalIgnoreCase));
        if (info is null) throw new McpException($"Named credential '{credential}' does not exist.");

        var commandFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command))).ToLowerInvariant()[..16];
        if (!info.IsAllowedForTool(toolName))
        {
            await audit.WriteCredentialUsageAsync(info.Name, toolName, $"cmd:{commandFingerprint}",
                "denied:tool-policy", cancellationToken);
            throw new McpException($"Credential '{info.Name}' is not authorized for tool '{toolName}'.");
        }
        if (!injectionRateLimiter.TryAcquire(info.Name, out var retryAfter))
        {
            await audit.WriteCredentialUsageAsync(info.Name, toolName, $"cmd:{commandFingerprint}",
                "denied:rate-limit", cancellationToken);
            throw new McpException($"Credential injection rate limit exceeded. Retry after {Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))} seconds.");
        }

        var approvalTarget = $"{info.Name}@cmd:{commandFingerprint}";
        var decision = await approvals.RequestAsync("secret.use", approvalTarget,
            $"Use credential {info.Name} in shell session {sessionId[..Math.Min(8, sessionId.Length)]}: {Trim(command)}", cancellationToken);
        if (decision == ApprovalDecision.Deny)
        {
            await audit.WriteCredentialUsageAsync(info.Name, toolName, $"cmd:{commandFingerprint}", "denied:approval", cancellationToken);
            throw new McpException("Credential use denied by local user.");
        }
        if (decision == ApprovalDecision.Timeout)
        {
            await audit.WriteCredentialUsageAsync(info.Name, toolName, $"cmd:{commandFingerprint}", "denied:approval-timeout", cancellationToken);
            throw new McpException("Credential use approval timed out.");
        }

        var value = await secrets.ResolveAsync(info.Name, cancellationToken);
        if (value is null) throw new McpException($"Named credential '{info.Name}' could not be resolved from the local secure store.");
        try
        {
            await sessions.WriteSecretAsync(sessionId, value, submit, cancellationToken);
            await audit.WriteCredentialUsageAsync(info.Name, toolName, $"cmd:{commandFingerprint}", "injected", cancellationToken);
            return new { sessionId, credential = info.Name, injected = true, submit };
        }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }
        finally { value = null; }
    }

    private static string Trim(string value) => value.Length <= 500 ? value : value[..500] + "…";
}
