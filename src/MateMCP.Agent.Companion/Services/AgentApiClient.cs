using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace MateMCP.Agent.Companion.Services;

public sealed class AgentApiClient : IDisposable
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AgentApiClient()
    {
        var configured = Environment.GetEnvironmentVariable("MATEMCP_COMPANION_AGENT_URL");
        var baseUrl = string.IsNullOrWhiteSpace(configured) ? "http://127.0.0.1:45871/" : configured.TrimEnd('/') + "/";
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<AgentStatus?> GetStatusAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<AgentStatus>("status", Json, ct);

    public async Task<DeviceManagementStatus?> GetDevicesAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<DeviceManagementStatus>("devices", Json, ct);

    public async Task EnableEnrollmentAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("devices/enrollment/enable", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task SignOutCurrentDeviceAsync(CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync("devices/current", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task RevokeDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"devices/{Uri.EscapeDataString(deviceId)}", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<DesktopBackgroundUpdateStatus?> GetDesktopUpdateStatusAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<DesktopBackgroundUpdateStatus>("desktop-update", Json, ct);

    public async Task<DesktopBackgroundUpdateStatus?> SetDesktopAutoUpdateAsync(bool enabled, CancellationToken ct = default)
    {
        using var response = await _http.PutAsJsonAsync("desktop-update/auto", new { Enabled = enabled }, Json, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DesktopBackgroundUpdateStatus>(Json, ct);
    }

    public async Task<IReadOnlyList<PendingApproval>> GetApprovalsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<PendingApproval>>("approvals", Json, ct) ?? [];

    public async Task MarkApprovalNotificationsReadyAsync(CancellationToken ct = default)
    {
        using var response = await _http.PostAsync("companion/notifications/ready", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DecideApprovalAsync(string id, string decision, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync($"approvals/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(decision)}", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<UserSecretInfo>> GetSecretsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<UserSecretInfo>>("secrets", Json, ct) ?? [];

    public async Task SaveSecretAsync(string name, string value, string? description, IReadOnlyCollection<string>? allowedTools, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("secrets", new
        {
            Name = name,
            Value = value,
            Description = description,
            Kind = 0,
            AllowedTools = allowedTools
        }, Json, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteSecretAsync(string name, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"secrets/{Uri.EscapeDataString(name)}", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<ShellSessionSnapshot>> GetShellSessionsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<ShellSessionSnapshot>>("shell/sessions", Json, ct) ?? [];

    public async Task<ShellSessionSnapshot?> ReadShellSessionAsync(string id, int offset, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(
            $"shell/sessions/{Uri.EscapeDataString(id)}?offset={Math.Max(0, offset)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ShellSessionSnapshot>(Json, ct);
    }

    public async Task SendShellInputAsync(string id, string text, bool submit = true, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync($"shell/sessions/{Uri.EscapeDataString(id)}/input", new { Text = text, Submit = submit }, Json, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task CloseShellSessionAsync(string id, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"shell/sessions/{Uri.EscapeDataString(id)}", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(int limit = 200, DateTimeOffset? from = null,
        DateTimeOffset? to = null, CancellationToken ct = default)
    {
        var query = $"audit?limit={Math.Clamp(limit, 1, 1000)}";
        if (from is not null) query += "&from=" + Uri.EscapeDataString(from.Value.ToString("o", CultureInfo.InvariantCulture));
        if (to is not null) query += "&to=" + Uri.EscapeDataString(to.Value.ToString("o", CultureInfo.InvariantCulture));
        return await _http.GetFromJsonAsync<List<AuditEntry>>(query, Json, ct) ?? [];
    }

    public async Task<int> CleanupAuditAsync(DateTimeOffset before, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync(
            "audit?before=" + Uri.EscapeDataString(before.ToString("o", CultureInfo.InvariantCulture)), ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<AuditCleanupResult>(Json, ct);
        return result?.Deleted ?? 0;
    }

    public void Dispose() => _http.Dispose();
}

public sealed record AgentStatus(string Service, string Endpoint, string Management, string Configuration, IReadOnlyList<string> Projects,
    bool ShellApproval, int InteractiveSessions, RelayStatus Relay, string Credentials);

public sealed record DeviceManagementStatus(bool Enrolled, bool EnrollmentSuppressed, string? CurrentDeviceId,
    IReadOnlyList<ManagedDevice> Devices, string? UpstreamError = null);
public sealed record ManagedDevice(string Id, string Name, string Platform, string Status, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, string McpUrl, bool IsCurrent);

public sealed record DesktopBackgroundUpdateStatus(bool AutoUpdateEnabled, string State, string Message,
    DateTimeOffset? LastChangedAt, long InstalledAssetId, string? LastFailure);
public sealed record RelayStatus(bool Enabled, string? Url, string? DeviceId, bool EnrollmentSuppressed = false);
public sealed record PendingApproval(string Id, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Capability, string Target, string Summary);
public sealed record UserSecretInfo(string Name, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Kind, IReadOnlyList<string>? AllowedTools);
public sealed record ShellSessionSnapshot(string SessionId, int ProcessId, string Output, int NextOffset, bool OutputTruncated, bool Exited,
    int? ExitCode, string WorkingDirectory, DateTimeOffset CreatedAt, DateTimeOffset LastTouched);
public sealed record AuditEntry(DateTimeOffset Timestamp, string Capability, string Target, string Result, string? Credential, string? Tool);
public sealed record AuditCleanupResult(int Deleted);
