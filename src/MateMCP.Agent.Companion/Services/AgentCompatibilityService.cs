using System.Reflection;
using System.Text.Json;

namespace MateMCP.Agent.Companion.Services;

public sealed class AgentCompatibilityService : IDisposable
{
    private static readonly string[] RequiredTools =
    [
        "memory_search",
        "memory_applicable",
        "memory_read",
        "memory_create",
        "memory_update",
        "memory_delete"
    ];

    private readonly HttpClient _http;

    public AgentCompatibilityService()
    {
        var configured = Environment.GetEnvironmentVariable("MATEMCP_COMPANION_AGENT_URL");
        var baseUrl = string.IsNullOrWhiteSpace(configured) ? "http://127.0.0.1:45871/" : configured.TrimEnd('/') + "/";
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(5) };
    }

    public async Task<AgentCompatibilityStatus> CheckAsync(CancellationToken ct = default)
    {
        var companionVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        try
        {
            using var response = await _http.GetAsync("status", ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = json.RootElement;

            if (!root.TryGetProperty("mcpTools", out var tools)
                || !tools.TryGetProperty("revision", out var revisionElement)
                || string.IsNullOrWhiteSpace(revisionElement.GetString())
                || !tools.TryGetProperty("names", out var namesElement)
                || namesElement.ValueKind != JsonValueKind.Array)
            {
                return new AgentCompatibilityStatus(false, companionVersion, null, null,
                    "The running Agent is older than this Companion and does not expose the capability handshake. Update/restart the Agent.", RequiredTools);
            }

            var revision = revisionElement.GetString();
            var names = namesElement.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            var missing = RequiredTools.Where(x => !names.Contains(x)).ToArray();
            if (missing.Length > 0)
            {
                return new AgentCompatibilityStatus(false, companionVersion, "MateMCP", revision,
                    $"The running Agent is missing {missing.Length} capability/capabilities required by this Companion. Update/restart the Agent.", missing);
            }

            return new AgentCompatibilityStatus(true, companionVersion, "MateMCP", revision,
                "Companion and the running Agent are compatible.", []);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new AgentCompatibilityStatus(false, companionVersion, null, null,
                "Timed out while verifying the running Agent after update.", RequiredTools);
        }
        catch (Exception ex)
        {
            return new AgentCompatibilityStatus(false, companionVersion, null, null,
                $"Could not verify the running Agent: {ex.Message}", RequiredTools);
        }
    }

    public void Dispose() => _http.Dispose();
}

public sealed record AgentCompatibilityStatus(
    bool Compatible,
    string CompanionVersion,
    string? AgentProduct,
    string? CapabilityRevision,
    string Message,
    IReadOnlyList<string> MissingCapabilities);
