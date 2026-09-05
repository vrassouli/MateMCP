using System.Net.WebSockets;
using System.Text;
using MateMCP.Relay;
using Microsoft.Extensions.Logging.Abstractions;

namespace MateMCP.Relay.Tests;

public sealed class AgentRegistryConcurrencyTests
{
    [Fact]
    public void Replacement_connection_owns_generation_and_stale_remove_is_ignored()
    {
        var registry = NewRegistry();
        using var firstSocket = new TestWebSocket();
        using var secondSocket = new TestWebSocket();

        Assert.True(registry.TryRegister("device", firstSocket, "11111111111111111111111111111111", CancellationToken.None, out var first));
        Assert.True(registry.TryRegister("device", secondSocket, "22222222222222222222222222222222", CancellationToken.None, out var second));

        Assert.True(firstSocket.Aborted);
        Assert.False(registry.Remove("device", first));
        Assert.True(registry.TryGet("device", out var current));
        Assert.Same(second, current);
        Assert.Equal("22222222222222222222222222222222", current.ConnectionId);
        Assert.True(registry.Remove("device", second));
        Assert.False(registry.TryGet("device", out _));
    }

    [Fact]
    public void TryGet_only_returns_current_generation_after_replacement()
    {
        var registry = NewRegistry();
        using var firstSocket = new TestWebSocket();
        using var secondSocket = new TestWebSocket();

        registry.TryRegister("device", firstSocket, null, CancellationToken.None, out var first);
        registry.TryRegister("device", secondSocket, null, CancellationToken.None, out var second);

        Assert.NotEqual(first.ConnectionId, second.ConnectionId);
        Assert.True(registry.TryGet("device", out var current));
        Assert.Same(second, current);
    }

    [Fact]
    public async Task Cancelling_one_client_does_not_cancel_shared_websocket_or_another_request()
    {
        using var transportLifetime = new CancellationTokenSource();
        using var socket = new TestWebSocket(blockFirstSend: true);
        using var connection = new AgentConnection("device", socket, Guid.NewGuid().ToString("N"), transportLifetime.Token);
        using var clientCancellation = new CancellationTokenSource();

        var first = connection.SendAsync(Request("first"), TimeSpan.FromSeconds(5), clientCancellation.Token);
        await socket.FirstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clientCancellation.Cancel();

        var second = connection.SendAsync(Request("second"), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(socket.FirstObservedSendToken.IsCancellationRequested);
        Assert.False(transportLifetime.IsCancellationRequested);
        Assert.Equal(WebSocketState.Open, socket.State);

        socket.ReleaseFirstSend();
        await socket.SecondSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(connection.Complete(Response("second")));

        var secondResponse = await second;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        Assert.Equal("second", secondResponse.Id);
        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.False(socket.Aborted);
        Assert.False(socket.FirstObservedSendToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Connection_lifetime_cancellation_can_cancel_shared_transport_send()
    {
        using var transportLifetime = new CancellationTokenSource();
        using var socket = new TestWebSocket(blockFirstSend: true);
        using var connection = new AgentConnection("device", socket, Guid.NewGuid().ToString("N"), transportLifetime.Token);

        var requestTask = connection.SendAsync(Request("blocked"), TimeSpan.FromSeconds(10), CancellationToken.None);
        await socket.FirstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        transportLifetime.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
        Assert.True(socket.FirstObservedSendToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Two_concurrent_clients_share_one_registered_agent_without_registry_gap()
    {
        var registry = NewRegistry();
        using var socket = new TestWebSocket();
        Assert.True(registry.TryRegister("shared-device", socket, Guid.NewGuid().ToString("N"), CancellationToken.None, out var connection));
        using (connection)
        {
            var first = connection.SendAsync(Request("client-one"), TimeSpan.FromSeconds(2), CancellationToken.None);
            var second = connection.SendAsync(Request("client-two"), TimeSpan.FromSeconds(2), CancellationToken.None);

            await socket.SecondSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(registry.TryGet("shared-device", out var current));
            Assert.Same(connection, current);
            Assert.Equal(2, connection.PendingRequestCount);

            Assert.True(connection.Complete(Response("client-two")));
            Assert.True(connection.Complete(Response("client-one")));
            var responses = await Task.WhenAll(first, second);

            Assert.Equal(2, responses.Length);
            Assert.True(registry.TryGet("shared-device", out current));
            Assert.Same(connection, current);
            Assert.Equal(0, connection.PendingRequestCount);
        }
    }

    [Fact]
    public async Task Timeout_of_one_client_cleans_only_its_pending_request_and_keeps_other_active()
    {
        using var socket = new TestWebSocket();
        using var connection = new AgentConnection("device", socket, Guid.NewGuid().ToString("N"));

        var timedOut = connection.SendAsync(Request("timeout"), TimeSpan.FromMilliseconds(100), CancellationToken.None);
        var active = connection.SendAsync(Request("active"), TimeSpan.FromSeconds(2), CancellationToken.None);
        await socket.SecondSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAsync<TimeoutException>(() => timedOut);
        Assert.Equal(1, connection.PendingRequestCount);
        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.False(socket.Aborted);

        Assert.True(connection.Complete(Response("active")));
        var response = await active;

        Assert.Equal("active", response.Id);
        Assert.Equal(0, connection.PendingRequestCount);
        Assert.Equal(WebSocketState.Open, socket.State);
    }
    private static AgentRegistry NewRegistry()
        => new(NullLogger<AgentRegistry>.Instance, new RelayInstanceIdentity());

    private static RelayRequest Request(string id) => new(id, "POST", "/mcp", new(), null);
    private static RelayResponse Response(string id) => new(id, 200, new(), null, null);

    private sealed class TestWebSocket(bool blockFirstSend = false) : WebSocket
    {
        private readonly TaskCompletionSource _releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sendCount;
        private WebSocketState _state = WebSocketState.Open;

        public TaskCompletionSource FirstSendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondSendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken FirstObservedSendToken { get; private set; }
        public bool Aborted { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public void ReleaseFirstSend() => _releaseFirst.TrySetResult();

        public override void Abort()
        {
            Aborted = true;
            _state = WebSocketState.Aborted;
            _releaseFirst.TrySetCanceled();
        }

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

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _releaseFirst.TrySetResult();
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _sendCount);
            if (count == 1)
            {
                FirstObservedSendToken = cancellationToken;
                FirstSendStarted.TrySetResult();
                if (blockFirstSend) await _releaseFirst.Task.WaitAsync(cancellationToken);
            }
            else if (count == 2)
            {
                SecondSendStarted.TrySetResult();
            }
        }
    }
}
