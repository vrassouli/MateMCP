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

    private static readonly string[] RequiredManagementCapabilities =
    [
        "projects-stable-id",
        "skills-memory",
        "desktop-update",
        "agent-logs"
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

            var agentVersion = root.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == JsonValueKind.String
                ? versionElement.GetString()
                : null;

            if (!root.TryGetProperty("mcpTools", out var tools)
                || !tools.TryGetProperty("revision", out var revisionElement)
                || string.IsNullOrWhiteSpace(revisionElement.GetString())
                || !tools.TryGetProperty("names", out var namesElement)
                || namesElement.ValueKind != JsonValueKind.Array)
            {
                return Incompatible(companionVersion, agentVersion, null, null,
                    "The running Agent is older than this Companion and does not expose the MCP capability handshake. Update/restart the Agent.",
                    RequiredTools.Select(x => $"mcp:{x}").ToArray());
            }

            var toolRevision = revisionElement.GetString();
            var names = namesElement.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            var missingTools = RequiredTools.Where(x => !names.Contains(x)).Select(x => $"mcp:{x}").ToArray();
            if (missingTools.Length > 0)
            {
                return Incompatible(companionVersion, agentVersion, toolRevision, null,
                    $"The running Agent is missing {missingTools.Length} MCP capability/capabilities required by this Companion. Update/restart the Agent.",
                    missingTools);
            }

            if (!root.TryGetProperty("managementApi", out var managementApi)
                || !managementApi.TryGetProperty("revision", out var managementRevisionElement)
                || managementRevisionElement.ValueKind != JsonValueKind.Number
                || !managementRevisionElement.TryGetInt32(out var managementRevision)
                || !managementApi.TryGetProperty("capabilities", out var managementCapabilitiesElement)
                || managementCapabilitiesElement.ValueKind != JsonValueKind.Array)
            {
                return Incompatible(companionVersion, agentVersion, toolRevision, null,
                    "The running Agent exposes MCP memory tools but not the local management API required by this Companion. Update/restart the Agent.",
                    RequiredManagementCapabilities.Select(x => $"api:{x}").ToArray());
            }

            var managementCapabilities = managementCapabilitiesElement.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToHashSet(StringComparer.Ordinal);
            var missingManagement = RequiredManagementCapabilities
                .Where(x => !managementCapabilities.Contains(x))
                .Select(x => $"api:{x}")
                .ToArray();
            if (missingManagement.Length > 0)
            {
                return Incompatible(companionVersion, agentVersion, toolRevision, managementRevision,
                    $"The running Agent is missing {missingManagement.Length} local management capability/capabilities required by this Companion. Update/restart the Agent.",
                    missingManagement);
            }

            var failedProbe = await ProbeManagementApiAsync(ct);
            if (failedProbe is not null)
            {
                return Incompatible(companionVersion, agentVersion, toolRevision, managementRevision,
                    $"The running Agent advertises the required management API, but {failedProbe} is not reachable. Repair/update the Agent and restart it.",
                    [$"api:{failedProbe}"]);
            }

            return new AgentCompatibilityStatus(true, companionVersion, "MateMCP", agentVersion, toolRevision, managementRevision,
                "Companion and the running Agent are compatible.", []);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Incompatible(companionVersion, null, null, null,
                "Timed out while verifying the running Agent after update.",
                RequiredManagementCapabilities.Select(x => $"api:{x}").ToArray());
        }
        catch (Exception ex)
        {
            return Incompatible(companionVersion, null, null, null,
                $"Could not verify the running Agent: {ex.Message}",
                RequiredManagementCapabilities.Select(x => $"api:{x}").ToArray());
        }
    }

    private async Task<string?> ProbeManagementApiAsync(CancellationToken ct)
    {
        foreach (var endpoint in new[] { "skills-memory?includeDisabled=true", "projects", "desktop-update", "logs?limit=1" })
        {
            using var response = await _http.GetAsync(endpoint, ct);
            if (!response.IsSuccessStatusCode)
                return endpoint.Split('?', 2)[0];
        }
        return null;
    }

    private static AgentCompatibilityStatus Incompatible(
        string companionVersion,
        string? agentVersion,
        string? toolRevision,
        int? managementRevision,
        string message,
        IReadOnlyList<string> missing)
        => new(false, companionVersion, agentVersion is null ? null : "MateMCP", agentVersion, toolRevision, managementRevision, message, missing);

    public void Dispose() => _http.Dispose();
}

public sealed record AgentCompatibilityStatus(
    bool Compatible,
    string CompanionVersion,
    string? AgentProduct,
    string? AgentVersion,
    string? CapabilityRevision,
    int? ManagementApiRevision,
    string Message,
    IReadOnlyList<string> MissingCapabilities);
