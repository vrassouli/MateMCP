using System.Net.WebSockets;
using System.Text;
using MateMCP.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MateMCP.Relay.Tests;

public sealed class RelayBackpressureTests
{
    [Fact]
    public async Task Saturated_connection_returns_agent_busy_without_disturbing_inflight_request()
    {
        using var socket = new CountingWebSocket();
        using var connection = new AgentConnection("device", socket, "connection", maxPendingRequests: 1);

        var first = connection.SendAsync(Request("first"), TimeSpan.FromSeconds(2), CancellationToken.None);
        await socket.WaitForSendAsync();
        Assert.Equal(1, connection.PendingRequestCount);

        var busy = await connection.SendAsync(Request("busy"), TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(429, busy.StatusCode);
        Assert.Equal("agent_busy", busy.Error);
        Assert.Equal("1", busy.Headers["Retry-After"].Single());
        Assert.Equal("agent_busy", busy.Headers["X-MateMCP-Backpressure"].Single());
        Assert.Equal("1", busy.Headers["X-MateMCP-Agent-Pending"].Single());
        Assert.Equal("1", busy.Headers["X-MateMCP-Agent-Capacity"].Single());
        Assert.Contains("\"error\":\"agent_busy\"", DecodeBody(busy));
        Assert.Equal(1, connection.PendingRequestCount);
        Assert.Equal(1, socket.SendCount);

        Assert.True(connection.Complete(Response("first")));
        Assert.Equal("first", (await first).Id);
        Assert.Equal(0, connection.PendingRequestCount);

        var afterRecovery = connection.SendAsync(Request("after"), TimeSpan.FromSeconds(2), CancellationToken.None);
        await socket.WaitForSendAsync();
        Assert.True(connection.Complete(Response("after")));
        Assert.Equal("after", (await afterRecovery).Id);
        Assert.Equal(0, connection.PendingRequestCount);
        Assert.Equal(2, socket.SendCount);
    }

    [Fact]
    public async Task Cancellation_reclaims_admission_capacity()
    {
        using var socket = new CountingWebSocket();
        using var connection = new AgentConnection("device", socket, "connection", maxPendingRequests: 1);
        using var cancellation = new CancellationTokenSource();

        var cancelled = connection.SendAsync(Request("cancelled"), TimeSpan.FromSeconds(5), cancellation.Token);
        await socket.WaitForSendAsync();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(0, connection.PendingRequestCount);

        var next = connection.SendAsync(Request("next"), TimeSpan.FromSeconds(2), CancellationToken.None);
        await socket.WaitForSendAsync();
        Assert.True(connection.Complete(Response("next")));
        Assert.Equal("next", (await next).Id);
        Assert.Equal(0, connection.PendingRequestCount);
    }

    [Fact]
    public async Task Timeout_reclaims_admission_capacity()
    {
        using var socket = new CountingWebSocket();
        using var connection = new AgentConnection("device", socket, "connection", maxPendingRequests: 1);

        var timedOut = connection.SendAsync(Request("timeout"), TimeSpan.FromMilliseconds(50), CancellationToken.None);
        await socket.WaitForSendAsync();

        await Assert.ThrowsAsync<TimeoutException>(() => timedOut);
        Assert.Equal(0, connection.PendingRequestCount);

        var next = connection.SendAsync(Request("next"), TimeSpan.FromSeconds(2), CancellationToken.None);
        await socket.WaitForSendAsync();
        Assert.True(connection.Complete(Response("next")));
        Assert.Equal("next", (await next).Id);
    }

    [Fact]
    public async Task Transport_disconnect_reclaims_admission_capacity()
    {
        using var socket = new CountingWebSocket();
        using var connection = new AgentConnection("device", socket, "connection", maxPendingRequests: 1);

        var pending = connection.SendAsync(Request("pending"), TimeSpan.FromSeconds(5), CancellationToken.None);
        await socket.WaitForSendAsync();
        Assert.Equal(1, connection.PendingRequestCount);

        connection.Disconnect();

        await Assert.ThrowsAsync<AgentTransportLostException>(() => pending);
        Assert.Equal(0, connection.PendingRequestCount);
    }

    [Fact]
    public async Task Registry_applies_configured_capacity_per_agent_and_isolates_overload()
    {
        var registry = new AgentRegistry(
            NullLogger<AgentRegistry>.Instance,
            new RelayInstanceIdentity(),
            Options.Create(new RelayOptions
            {
                AgentReconnectGraceSeconds = 20,
                MaxPendingRequestsPerAgent = 1
            }));
        using var socketA = new CountingWebSocket();
        using var socketB = new CountingWebSocket();

        registry.TryRegister("device-a", socketA, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", CancellationToken.None, out var agentA);
        registry.TryRegister("device-b", socketB, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", CancellationToken.None, out var agentB);
        using (agentA)
        using (agentB)
        {
            Assert.Equal(1, agentA.PendingRequestCapacity);
            Assert.Equal(1, agentB.PendingRequestCapacity);
            Assert.Equal(1, registry.Snapshot("device-a").PendingCapacity);
            Assert.Equal(1, registry.Snapshot("device-b").PendingCapacity);

            var aFirst = agentA.SendAsync(Request("a-first"), TimeSpan.FromSeconds(2), CancellationToken.None);
            await socketA.WaitForSendAsync();
            var aBusy = await agentA.SendAsync(Request("a-busy"), TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.Equal(429, aBusy.StatusCode);

            var bFirst = agentB.SendAsync(Request("b-first"), TimeSpan.FromSeconds(2), CancellationToken.None);
            await socketB.WaitForSendAsync();
            Assert.Equal(1, agentB.PendingRequestCount);
            Assert.Equal(1, socketB.SendCount);

            Assert.True(agentA.Complete(Response("a-first")));
            Assert.True(agentB.Complete(Response("b-first")));
            await Task.WhenAll(aFirst, bFirst);

            Assert.Equal(0, agentA.PendingRequestCount);
            Assert.Equal(0, agentB.PendingRequestCount);
        }
    }

    private static RelayRequest Request(string id)
        => new(id, "POST", "/mcp", new Dictionary<string, string[]>
        {
            ["Mcp-Session-Id"] = [$"session-{id}"]
        }, null, $"operation-{id}");

    private static RelayResponse Response(string id) => new(id, 200, new(), null, null);

    private static string DecodeBody(RelayResponse response)
        => Encoding.UTF8.GetString(Convert.FromBase64String(response.BodyBase64!));

    private sealed class CountingWebSocket : WebSocket
    {
        private readonly SemaphoreSlim _sendSignals = new(0);
        private WebSocketState _state = WebSocketState.Open;
        private int _sendCount;

        public int SendCount => Volatile.Read(ref _sendCount);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public async Task WaitForSendAsync()
            => await _sendSignals.WaitAsync(TimeSpan.FromSeconds(2));

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

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
            _sendSignals.Dispose();
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            _sendSignals.Release();
            return Task.CompletedTask;
        }
    }
}
