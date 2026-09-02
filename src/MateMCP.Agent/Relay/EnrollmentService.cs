using System.Diagnostics;
using System.Net.Http.Json;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Security;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Relay;

public sealed class EnrollmentService(
    IOptionsMonitor<MateOptions> options,
    IHttpClientFactory clients,
    AgentCredentialStore credentials,
    EnrollmentStateStore state,
    ILogger<EnrollmentService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var current = options.CurrentValue;
        if (!current.Relay.Enabled || current.Relay.EnrollmentSuppressed) return;

        string? recoverAgentId = null;
        if (!string.IsNullOrWhiteSpace(current.Relay.DeviceId))
        {
            var existingCredential = await credentials.GetAsync(current.Relay.DeviceId, ct);
            if (!string.IsNullOrWhiteSpace(existingCredential)) return;

            recoverAgentId = current.Relay.DeviceId;
            logger.LogWarning("Agent {DeviceId} has no credential in the OS secure credential store; starting identity recovery while preserving the existing MCP URL.", recoverAgentId);
        }

        var client = clients.CreateClient();
        client.BaseAddress = new Uri(current.Relay.ControlPlaneUrl.TrimEnd('/') + "/");
        var response = await client.PostAsJsonAsync("api/enrollment/start", new
        {
            name = Environment.MachineName,
            platform = GetPlatformDescription(),
            recoverAgentId
        }, ct);
        response.EnsureSuccessStatusCode();
        var enrollment = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Invalid enrollment response.");

        if (recoverAgentId is null)
            logger.LogWarning("Sign in to MateMCP and approve this Agent: {Url} (code {Code})", enrollment.VerificationUriComplete, enrollment.UserCode);
        else
            logger.LogWarning("Sign in to MateMCP and approve recovery of Agent {DeviceId}: {Url} (code {Code})", recoverAgentId, enrollment.VerificationUriComplete, enrollment.UserCode);

        OpenBrowser(enrollment.VerificationUriComplete, logger);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(enrollment.ExpiresIn);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, enrollment.Interval)), ct);
            using var tokenResponse = await client.PostAsJsonAsync("api/enrollment/token", new { deviceCode = enrollment.DeviceCode }, ct);
            if ((int)tokenResponse.StatusCode == 428) continue;
            tokenResponse.EnsureSuccessStatusCode();
            var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
                ?? throw new InvalidOperationException("Invalid Agent token response.");

            if (recoverAgentId is not null && !string.Equals(token.AgentId, recoverAgentId, StringComparison.Ordinal))
                throw new InvalidOperationException($"MateMCP recovery returned a different Agent identity ({token.AgentId}) than the requested identity ({recoverAgentId}). Refusing to replace the existing MCP URL.");

            await credentials.SaveAsync(token.AgentId, token.Credential, ct);
            state.MarkEnrolled(token.AgentId);
            logger.LogInformation(recoverAgentId is null ? "Agent enrolled. MCP URL: {McpUrl}" : "Agent credential recovered. MCP URL remains {McpUrl}", token.McpUrl);
            return;
        }

        throw new TimeoutException(recoverAgentId is null ? "MateMCP Agent enrollment expired." : "MateMCP Agent recovery expired.");
    }

    private static string GetPlatformDescription()
    {
        if (OperatingSystem.IsWindows())
            return $"Windows {Environment.OSVersion.Version}";
        if (OperatingSystem.IsMacOS())
            return $"macOS {Environment.OSVersion.Version}";
        if (OperatingSystem.IsLinux())
            return $"Linux {Environment.OSVersion.Version}";
        return Environment.OSVersion.ToString();
    }

    private static void OpenBrowser(string url, ILogger logger)
    {
        try
        {
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
            {
                start = new ProcessStartInfo(url) { UseShellExecute = true };
            }
            else if (OperatingSystem.IsMacOS())
            {
                start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
                start.ArgumentList.Add(url);
            }
            else if (OperatingSystem.IsLinux())
            {
                start = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                start.ArgumentList.Add(url);
            }
            else
            {
                return;
            }

            Process.Start(start)?.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open the MateMCP sign-in page automatically. Open this URL manually: {Url}", url);
        }
    }

    private sealed record EnrollmentResponse(string DeviceCode, string UserCode, string VerificationUriComplete, int Interval, int ExpiresIn);
    private sealed record TokenResponse(string AgentId, string Credential, string McpUrl);
}
