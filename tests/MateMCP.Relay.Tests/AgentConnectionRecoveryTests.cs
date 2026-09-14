using System.Net.WebSockets;
using MateMCP.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MateMCP.Relay.Tests;

public sealed class AgentConnectionRecoveryTests
{
    [Fact]
    public async Task Disconnect_fails_pending_request_immediately_with_transport_lost()
    {
        using var socket = new TestWebSocket();
        using var connection = new AgentConnection("device", socket, "connection-1");
        var pending = connection.SendAsync(Request("request-1", "operation-1"), TimeSpan.FromSeconds(30), CancellationToken.None);
        await socket.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        connection.Disconnect();

        var failure = await Assert.ThrowsAsync<AgentTransportLostException>(() => pending);
        Assert.Contains("connection-1", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, connection.PendingRequestCount);
    }

    [Fact]
    public async Task Wait_for_online_returns_replacement_connection_after_reconnect()
    {
        var registry = NewRegistry();
        using var firstSocket = new TestWebSocket();
        using var secondSocket = new TestWebSocket();

        registry.TryRegister("device", firstSocket, "11111111111111111111111111111111", CancellationToken.None, out var first);
        Assert.True(registry.Remove("device", first));

        var waiting = registry.WaitForOnlineAsync("device", first.ConnectionId, TimeSpan.FromSeconds(2), CancellationToken.None);
        await Task.Delay(25);
        registry.TryRegister("device", secondSocket, "22222222222222222222222222222222", CancellationToken.None, out var replacement);

        var recovered = await waiting;
        Assert.Same(replacement, recovered);
        Assert.Equal("22222222222222222222222222222222", recovered!.ConnectionId);
    }

    [Fact]
    public async Task Wait_for_online_returns_null_when_grace_expires_without_reconnect()
    {
        var registry = NewRegistry(graceSeconds: 1);
        using var socket = new TestWebSocket();
        registry.TryRegister("device", socket, null, CancellationToken.None, out var connection);
        Assert.True(registry.Remove("device", connection));

        var result = await registry.WaitForOnlineAsync("device", connection.ConnectionId, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Null(result);
    }

    private static AgentRegistry NewRegistry(int graceSeconds = 20)
        => new(
            NullLogger<AgentRegistry>.Instance,
            new RelayInstanceIdentity(),
            Options.Create(new RelayOptions { AgentReconnectGraceSeconds = graceSeconds }));

    private static RelayRequest Request(string id, string operationId)
        => new(id, "POST", "/mcp", new(), null, operationId);

    private sealed class TestWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SendStarted.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
