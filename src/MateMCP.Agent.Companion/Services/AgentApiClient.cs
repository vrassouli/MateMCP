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

    public async Task<IReadOnlyList<PendingApproval>> GetApprovalsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<PendingApproval>>("approvals", Json, ct) ?? [];

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
        => await _http.GetFromJsonAsync<ShellSessionSnapshot>($"shell/sessions/{Uri.EscapeDataString(id)}?offset={Math.Max(0, offset)}", Json, ct);

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

    public async Task<IReadOnlyList<AuditEntry>> GetAuditAsync(int limit = 200, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<AuditEntry>>($"audit?limit={Math.Clamp(limit, 1, 1000)}", Json, ct) ?? [];

    public void Dispose() => _http.Dispose();
}

public sealed record AgentStatus(string Service, string Endpoint, string Management, string Configuration, IReadOnlyList<string> Projects,
    bool ShellApproval, int InteractiveSessions, RelayStatus Relay, string Credentials);

public sealed record RelayStatus(bool Enabled, string? Url, string? DeviceId);
public sealed record PendingApproval(string Id, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Capability, string Target, string Summary);
public sealed record UserSecretInfo(string Name, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Kind, IReadOnlyList<string>? AllowedTools);
public sealed record ShellSessionSnapshot(string SessionId, int ProcessId, string Output, int NextOffset, bool OutputTruncated, bool Exited,
    int? ExitCode, string WorkingDirectory, DateTimeOffset CreatedAt, DateTimeOffset LastTouched);
public sealed record AuditEntry(DateTimeOffset Timestamp, string Capability, string Target, string Result, string? Credential, string? Tool);
