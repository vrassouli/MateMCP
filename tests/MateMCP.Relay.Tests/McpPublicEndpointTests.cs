using System.Text;
using System.Text.Json;
using MateMCP.Relay;

namespace MateMCP.Relay.Tests;

public sealed class McpPublicEndpointTests
{
    [Fact]
    public void ToolsListRequest_IsRecognizedWithoutTreatingNotificationsAsRequests()
    {
        Assert.True(McpPublicEndpoint.IsToolsListRequest(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")));
        Assert.False(McpPublicEndpoint.IsToolsListRequest(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","method":"tools/list"}""")));
        Assert.False(McpPublicEndpoint.IsToolsListRequest(Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_profile"}}""")));
    }

    [Fact]
    public void ProfileCall_UsesOnlyTheToolCallIdAndCannotSelectAnotherAgent()
    {
        var request = Encoding.UTF8.GetBytes(
            """{"jsonrpc":"2.0","id":"profile-42","method":"tools/call","params":{"name":"get_profile","arguments":{"agent_id":"agt_attacker_choice"}}}""");

        Assert.True(McpPublicEndpoint.TryGetProfileCallId(request, out var requestId));
        Assert.Equal("profile-42", requestId.GetString());
    }

    [Fact]
    public void ToolsListResponse_AddsOpenAiProfileToolOnceWithRestrictedSchema()
    {
        var response = CreateToolsListResponse("""
            {"jsonrpc":"2.0","id":1,"result":{"tools":[{"name":"existing_tool","inputSchema":{"type":"object"}}]}}
            """);

        var enriched = McpPublicEndpoint.AddProfileTool(response);
        var duplicateEnriched = McpPublicEndpoint.AddProfileTool(enriched);

        Assert.Equal(1, CountProfileTools(enriched));
        Assert.Equal(1, CountProfileTools(duplicateEnriched));

        using var json = JsonDocument.Parse(Convert.FromBase64String(enriched.BodyBase64!));
        var tool = json.RootElement.GetProperty("result").GetProperty("tools")[1];
        Assert.Equal("get_profile", tool.GetProperty("name").GetString());
        Assert.False(tool.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean());
        Assert.False(tool.GetProperty("outputSchema").GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("id", tool.GetProperty("outputSchema").GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.True(tool.GetProperty("_meta").GetProperty("openai/profile").GetBoolean());
        Assert.True(tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Equal(@"\S", tool.GetProperty("outputSchema").GetProperty("properties").GetProperty("id").GetProperty("pattern").GetString());
        Assert.Equal("email", tool.GetProperty("outputSchema").GetProperty("properties").GetProperty("email").GetProperty("format").GetString());
    }

    [Theory]
    [InlineData(503, "application/json")]
    [InlineData(200, "text/plain")]
    public void ToolsListResponse_DoesNotEnrichErrorsOrNonJsonBodies(int statusCode, string contentType)
    {
        var response = new RelayResponse(
            "response-1",
            statusCode,
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = [contentType] },
            Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"result":{"tools":[]}}""")),
            null);

        Assert.Same(response, McpPublicEndpoint.AddProfileTool(response));
    }

    [Fact]
    public void ProfileCallResponse_PreservesJsonRpcIdAndStructuredProfile()
    {
        using var idDocument = JsonDocument.Parse("\"call-7\"");
        using var profileDocument = JsonDocument.Parse("""
            {"id":"agt_selected","name":"Build Agent","email":"owner@example.test","nickname":"Windows"}
            """);

        var response = McpPublicEndpoint.CreateProfileCallResponse(
            idDocument.RootElement,
            profileDocument.RootElement);
        using var json = JsonDocument.Parse(response);

        Assert.Equal("call-7", json.RootElement.GetProperty("id").GetString());
        Assert.Equal("agt_selected", json.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("id").GetString());
        Assert.Equal(
            profileDocument.RootElement.GetRawText(),
            json.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    private static RelayResponse CreateToolsListResponse(string body)
        => new(
            "response-1",
            200,
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = ["application/json; charset=utf-8"] },
            Convert.ToBase64String(Encoding.UTF8.GetBytes(body)),
            null);

    private static int CountProfileTools(RelayResponse response)
    {
        using var json = JsonDocument.Parse(Convert.FromBase64String(response.BodyBase64!));
        return json.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Count(tool => tool.GetProperty("name").GetString() == "get_profile");
    }
}
