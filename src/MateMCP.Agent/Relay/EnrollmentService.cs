using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Security;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Relay;

public sealed class EnrollmentService(IOptionsMonitor<MateOptions> options, IHttpClientFactory clients, AgentCredentialStore credentials, ILogger<EnrollmentService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var current = options.CurrentValue;
        if (!current.Relay.Enabled || !string.IsNullOrWhiteSpace(current.Relay.DeviceId)) return;
        var client = clients.CreateClient(); client.BaseAddress = new Uri(current.Relay.ControlPlaneUrl.TrimEnd('/') + "/");
        var response = await client.PostAsJsonAsync("api/enrollment/start", new { name = Environment.MachineName, platform = $"macOS {Environment.OSVersion.Version}" }, ct);
        response.EnsureSuccessStatusCode(); var enrollment = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(cancellationToken: ct) ?? throw new InvalidOperationException("Invalid enrollment response.");
        logger.LogWarning("Sign in to MateMCP and approve this Agent: {Url} (code {Code})", enrollment.VerificationUriComplete, enrollment.UserCode);
        OpenBrowser(enrollment.VerificationUriComplete);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(enrollment.ExpiresIn);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, enrollment.Interval)), ct);
            using var tokenResponse = await client.PostAsJsonAsync("api/enrollment/token", new { deviceCode = enrollment.DeviceCode }, ct);
            if ((int)tokenResponse.StatusCode == 428) continue;
            tokenResponse.EnsureSuccessStatusCode(); var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct) ?? throw new InvalidOperationException("Invalid Agent token response.");
            await credentials.SaveAsync(token.AgentId, token.Credential, ct); SaveAgentId(token.AgentId);
            logger.LogInformation("Agent enrolled. MCP URL: {McpUrl}", token.McpUrl); return;
        }
        throw new TimeoutException("MateMCP Agent enrollment expired.");
    }

    private static void SaveAgentId(string agentId)
    {
        var path = ConfigurationBootstrap.EnsureUserConfiguration(); var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); var relay = root["Mate"]!["Relay"]!.AsObject(); relay["DeviceId"] = agentId;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void OpenBrowser(string url)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false }; start.ArgumentList.Add(url); Process.Start(start)?.Dispose();
    }

    private sealed record EnrollmentResponse(string DeviceCode, string UserCode, string VerificationUriComplete, int Interval, int ExpiresIn);
    private sealed record TokenResponse(string AgentId, string Credential, string McpUrl);
}
