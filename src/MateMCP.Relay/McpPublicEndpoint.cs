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

        var contentType = response.Headers.FirstOrDefault(header =>
            string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)).Value;
        if (contentType is null ||
            !contentType.Any(value => value.Contains("application/json", StringComparison.OrdinalIgnoreCase)))
            return response;

        try
        {
            var bytes = Convert.FromBase64String(response.BodyBase64);
            var root = JsonNode.Parse(bytes) as JsonObject;
            var tools = root?["result"]?["tools"] as JsonArray;
            if (tools is null || tools.Any(tool => string.Equals(tool?["name"]?.GetValue<string>(), ProfileToolName, StringComparison.Ordinal)))
                return response;

            tools.Add(JsonNode.Parse(ProfileToolJson));
            return response with { BodyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(root!.ToJsonString())) };
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return response;
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
