using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace MateMCP.Relay;

public sealed class AgentActivityReporter(
    IHttpClientFactory clients,
    IOptions<RelayOptions> relayOptions,
    ILogger<AgentActivityReporter> logger) : BackgroundService
{
    private readonly RelayOptions _options = relayOptions.Value;
    private readonly Channel<QueuedAgentActivity> _queue = Channel.CreateBounded<QueuedAgentActivity>(
        new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    public void Record(
        string agentId,
        string type,
        string status,
        string operation,
        string message,
        double? durationMs = null,
        string? requestId = null,
        string? project = null)
    {
        var level = status switch
        {
            "failure" => "error",
            "warning" => "warning",
            _ => "info"
        };

        _queue.Writer.TryWrite(new QueuedAgentActivity(
            agentId,
            type,
            status,
            level,
            operation,
            message,
            durationMs,
            requestId,
            project));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var activity in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "internal/agents/activity")
                {
                    Content = JsonContent.Create(activity)
                };
                request.Headers.Add("X-MateMCP-Internal-Key", _options.InternalApiKey);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var response = await clients.CreateClient("control-plane").SendAsync(request, timeout.Token);

                if (!response.IsSuccessStatusCode)
                {
                    logger.LogDebug(
                        "Control plane rejected Agent activity: device={DeviceId}; type={ActivityType}; statusCode={StatusCode}",
                        activity.AgentId,
                        activity.Type,
                        (int)response.StatusCode);
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(
                    "Timed out recording Agent activity: device={DeviceId}; type={ActivityType}",
                    activity.AgentId,
                    activity.Type);
            }
            catch (Exception ex)
            {
                logger.LogDebug(
                    ex,
                    "Failed recording Agent activity: device={DeviceId}; type={ActivityType}",
                    activity.AgentId,
                    activity.Type);
            }
        }
    }

    private sealed record QueuedAgentActivity(
        string AgentId,
        string Type,
        string Status,
        string Level,
        string Operation,
        string Message,
        double? DurationMs,
        string? RequestId,
        string? Project);
}

internal static class McpActivityOperation
{
    public static string Describe(string httpMethod, ReadOnlyMemory<byte> body)
    {
        if (body.Length == 0)
            return $"MCP {httpMethod.ToUpperInvariant()}";

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                return "MCP batch";

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("method", out var methodProperty) ||
                methodProperty.ValueKind != JsonValueKind.String)
                return $"MCP {httpMethod.ToUpperInvariant()}";

            var method = Clean(methodProperty.GetString(), 80);
            if (string.Equals(method, "tools/call", StringComparison.Ordinal) &&
                root.TryGetProperty("params", out var parameters) &&
                parameters.ValueKind == JsonValueKind.Object &&
                parameters.TryGetProperty("name", out var nameProperty) &&
                nameProperty.ValueKind == JsonValueKind.String)
            {
                var tool = Clean(nameProperty.GetString(), 100);
                return string.IsNullOrWhiteSpace(tool) ? method : $"{method} · {tool}";
            }

            return string.IsNullOrWhiteSpace(method) ? $"MCP {httpMethod.ToUpperInvariant()}" : method;
        }
        catch (JsonException)
        {
            return $"MCP {httpMethod.ToUpperInvariant()}";
        }
    }

    private static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var buffer = new char[Math.Min(value.Length, maxLength)];
        var length = 0;
        foreach (var ch in value.Trim())
        {
            if (length >= buffer.Length) break;
            if (char.IsLetterOrDigit(ch) || ch is '/' or '_' or '-' or '.' or ':' or ' ')
                buffer[length++] = ch;
        }

        return new string(buffer, 0, length);
    }
}