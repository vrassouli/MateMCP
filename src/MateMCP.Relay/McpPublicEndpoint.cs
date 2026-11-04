using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MateMCP.Relay;

/// <summary>OpenAI public-directory behavior layered on top of the legacy per-Agent MCP transport.</summary>
public static class McpPublicEndpoint
{
    public const string ProfileToolName = "get_profile";

    private const string ProfileToolJson = """
        {
          "name": "get_profile",
          "description": "Returns the Agent profile represented by this authenticated MateMCP connection.",
          "inputSchema": { "type": "object", "properties": {}, "additionalProperties": false },
          "outputSchema": {
            "$schema": "https://json-schema.org/draft/2020-12/schema",
            "type": "object",
            "properties": {
              "id": { "type": "string", "minLength": 1, "pattern": "\\S", "description": "Opaque, stable identifier for this Agent profile." },
              "name": { "type": "string", "description": "Agent display name." },
              "email": { "type": "string", "format": "email", "description": "MateMCP account email associated with this Agent." },
              "nickname": { "type": "string", "description": "Agent platform or distinguishing label." }
            },
            "required": [ "id" ],
            "additionalProperties": false
          },
          "annotations": { "readOnlyHint": true, "destructiveHint": false, "openWorldHint": false },
          "_meta": { "openai/profile": true }
        }
        """;

    public static bool IsToolsListRequest(ReadOnlySpan<byte> body)
        => TryReadJsonRpcRequest(body, out var method, out _, out _) && method == "tools/list";

    public static bool TryGetProfileCallId(ReadOnlySpan<byte> body, out JsonElement id)
    {
        id = default;
        if (!TryReadJsonRpcRequest(body, out var method, out var requestId, out var root) || method != "tools/call")
            return false;

        if (!root.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
            !string.Equals(name.GetString(), ProfileToolName, StringComparison.Ordinal))
            return false;

        id = requestId;
        return true;
    }

    public static byte[] CreateProfileCallResponse(JsonElement id, JsonElement profile)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WritePropertyName("result");
            writer.WriteStartObject();
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", profile.GetRawText());
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WritePropertyName("structuredContent");
            profile.WriteTo(writer);
            writer.WriteBoolean("isError", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static RelayResponse AddProfileTool(RelayResponse response)
    {
        if (response.StatusCode is < 200 or >= 300 || string.IsNullOrWhiteSpace(response.BodyBase64))
            return response;

        var contentTypes = response.Headers.FirstOrDefault(header =>
            string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)).Value;
        if (contentTypes is null)
            return response;

        if (contentTypes.Any(value => value.Contains("application/json", StringComparison.OrdinalIgnoreCase)))
            return AddProfileToolToJsonResponse(response);

        if (contentTypes.Any(value => value.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase)))
            return AddProfileToolToEventStreamResponse(response);

        return response;
    }

    private static RelayResponse AddProfileToolToJsonResponse(RelayResponse response)
    {
        try
        {
            var bytes = Convert.FromBase64String(response.BodyBase64!);
            var enriched = TryAddProfileToolToJson(bytes);
            return enriched is null
                ? response
                : response with { BodyBase64 = Convert.ToBase64String(enriched) };
        }
        catch (FormatException)
        {
            return response;
        }
    }

    private static RelayResponse AddProfileToolToEventStreamResponse(RelayResponse response)
    {
        try
        {
            var body = Encoding.UTF8.GetString(Convert.FromBase64String(response.BodyBase64!));
            var lineEnding = body.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var frames = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split("\n\n");
            var changed = false;

            for (var frameIndex = 0; frameIndex < frames.Length; frameIndex++)
            {
                if (string.IsNullOrEmpty(frames[frameIndex]))
                    continue;

                var lines = frames[frameIndex].Split('\n').ToList();
                var dataLineIndexes = lines
                    .Select((line, index) => (line, index))
                    .Where(item => item.line.StartsWith("data:", StringComparison.Ordinal))
                    .Select(item => item.index)
                    .ToArray();
                if (dataLineIndexes.Length == 0)
                    continue;

                var payload = string.Join("\n", dataLineIndexes.Select(index =>
                {
                    var value = lines[index][5..];
                    return value.StartsWith(' ') ? value[1..] : value;
                }));

                var enriched = TryAddProfileToolToJson(Encoding.UTF8.GetBytes(payload));
                if (enriched is null)
                    continue;

                lines[dataLineIndexes[0]] = "data: " + Encoding.UTF8.GetString(enriched);
                for (var index = dataLineIndexes.Length - 1; index > 0; index--)
                    lines.RemoveAt(dataLineIndexes[index]);

                frames[frameIndex] = string.Join("\n", lines);
                changed = true;
            }

            if (!changed)
                return response;

            var enrichedBody = string.Join("\n\n", frames).Replace("\n", lineEnding, StringComparison.Ordinal);
            return response with { BodyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(enrichedBody)) };
        }
        catch (FormatException)
        {
            return response;
        }
    }

    private static byte[]? TryAddProfileToolToJson(ReadOnlySpan<byte> body)
    {
        try
        {
            var root = JsonNode.Parse(body) as JsonObject;
            var tools = root?["result"]?["tools"] as JsonArray;
            if (tools is null || tools.Any(tool => string.Equals(tool?["name"]?.GetValue<string>(), ProfileToolName, StringComparison.Ordinal)))
                return null;

            tools.Add(JsonNode.Parse(ProfileToolJson));
            return Encoding.UTF8.GetBytes(root!.ToJsonString());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryReadJsonRpcRequest(
        ReadOnlySpan<byte> body,
        out string? method,
        out JsonElement id,
        out JsonElement root)
    {
        method = null;
        id = default;
        root = default;
        try
        {
            using var document = JsonDocument.Parse(body.ToArray());
            root = document.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("id", out var idElement) || idElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return false;

            method = methodElement.GetString();
            id = idElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
