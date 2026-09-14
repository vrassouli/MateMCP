using System.Text.Json;
using MateMCP.Agent.Relay;
using Microsoft.Extensions.Logging.Abstractions;

namespace MateMCP.Agent.Tests;

public sealed class RelayLogicalSessionTests
{
    [Fact]
    public void Session_id_survives_relay_json_round_trip()
    {
        var original = new RelayRequest(
            "request-1",
            "POST",
            "/mcp",
            new() { ["Mcp-Session-Id"] = ["session-alpha"] },
            null,
            "operation-1");

        var json = JsonSerializer.Serialize(original);
        var copy = JsonSerializer.Deserialize<RelayRequest>(json);

        Assert.NotNull(copy);
        Assert.Equal("session-alpha", copy.SessionId);
        Assert.Equal("operation-1", copy.OperationId);
    }

    [Fact]
    public async Task Same_operation_id_cannot_cross_logical_sessions()
    {
        var registry = new RelayOperationRegistry(
            NullLogger<RelayOperationRegistry>.Instance,
            16,
            TimeSpan.FromMinutes(5),
            () => DateTimeOffset.UtcNow);

        var first = Request("r1", "shared-operation", "session-a");
        await registry.ExecuteAsync(
            first,
            _ => Task.FromResult(new RelayResponse("r1", 200, new(), null, null)),
            CancellationToken.None,
            CancellationToken.None);

        var second = Request("r2", "shared-operation", "session-b");
        await Assert.ThrowsAsync<RelayOperationConflictException>(() =>
            registry.ExecuteAsync(
                second,
                _ => Task.FromResult(new RelayResponse("r2", 200, new(), null, null)),
                CancellationToken.None,
                CancellationToken.None));
    }

    [Fact]
    public void Two_concurrent_client_sessions_are_distinct_on_same_agent_transport_model()
    {
        var first = Request("r1", "op-a", "session-a");
        var second = Request("r2", "op-b", "session-b");

        Assert.Equal("session-a", first.SessionId);
        Assert.Equal("session-b", second.SessionId);
        Assert.NotEqual(first.SessionId, second.SessionId);
    }

    private static RelayRequest Request(string id, string operationId, string sessionId)
        => new(
            id,
            "POST",
            "/mcp",
            new() { ["Mcp-Session-Id"] = [sessionId] },
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("same-payload")),
            operationId);
}
