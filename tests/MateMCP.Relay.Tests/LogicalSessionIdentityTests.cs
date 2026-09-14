using System.Net.WebSockets;
using MateMCP.Relay;

namespace MateMCP.Relay.Tests;

public sealed class LogicalSessionIdentityTests
{
    [Fact]
    public void Client_mcp_session_id_is_preserved()
    {
        var request = Request("r1", new() { ["Mcp-Session-Id"] = ["client-session-a"] });

        Assert.Equal("client-session-a", request.SessionId);
    }

    [Fact]
    public void Missing_session_id_is_generated_and_is_not_connection_identity()
    {
        var first = Request("r1", new());
        var second = Request("r2", new());

        Assert.False(string.IsNullOrWhiteSpace(first.SessionId));
        Assert.False(string.IsNullOrWhiteSpace(second.SessionId));
        Assert.NotEqual(first.SessionId, second.SessionId);
        Assert.Equal(32, first.SessionId.Length);
    }

    [Fact]
    public void Recovery_clone_preserves_session_and_operation_while_request_id_changes()
    {
        var original = Request("transport-1", new() { ["Mcp-Session-Id"] = ["session-1"] }, "operation-1");
        var recovered = original with { Id = "transport-2" };

        Assert.Equal("session-1", recovered.SessionId);
        Assert.Equal("operation-1", recovered.OperationId);
        Assert.Equal("transport-2", recovered.Id);
    }

    [Fact]
    public async Task Agent_response_returns_logical_mcp_session_id()
    {
        using var socket = new TestWebSocket();
        using var connection = new AgentConnection("device", socket, "physical-connection");
        var request = Request("r1", new() { ["Mcp-Session-Id"] = ["logical-session"] }, "op-1");

        var pending = connection.SendAsync(request, TimeSpan.FromSeconds(2), CancellationToken.None);
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(connection.Complete(new RelayResponse("r1", 200, new(), null, null)));
        var response = await pending;

        Assert.Equal("logical-session", Assert.Single(response.Headers["Mcp-Session-Id"]));
        Assert.NotEqual(connection.ConnectionId, response.Headers["Mcp-Session-Id"][0]);
    }

    private static RelayRequest Request(string id, Dictionary<string, string[]> headers, string? operationId = null)
        => new(id, "POST", "/mcp", headers, null, operationId);

    private sealed class TestWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { _state = WebSocketState.Closed; return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) { _state = WebSocketState.CloseSent; return Task.CompletedTask; }
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SendStarted.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
