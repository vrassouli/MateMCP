using System.Net;
using System.Net.WebSockets;
using MateMCP.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MateMCP.Relay.Tests;

public sealed class RelayShutdownDrainTests
{
    [Fact]
    public async Task Draining_connection_preserves_existing_request_and_rejects_new_work()
    {
        using var socket = new DrainTestWebSocket();
        using var connection = new AgentConnection("device", socket, "connection", maxPendingRequests: 4);

        var existing = connection.SendAsync(Request("existing"), TimeSpan.FromSeconds(2), CancellationToken.None);
        await socket.WaitForSendAsync();
        Assert.Equal(1, connection.PendingRequestCount);

        Assert.True(connection.BeginDrain());
        Assert.True(connection.IsDraining);

        var rejected = await connection.SendAsync(Request("new"), TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(503, rejected.StatusCode);
        Assert.Equal("relay_draining", rejected.Error);
        Assert.Equal("2", rejected.Headers["Retry-After"].Single());
        Assert.Equal("draining", rejected.Headers["X-MateMCP-Relay-State"].Single());
        Assert.Equal(1, socket.SendCount);
        Assert.Equal(1, connection.PendingRequestCount);

        Assert.True(connection.Complete(Response("existing")));
        Assert.Equal("existing", (await existing).Id);
        Assert.Equal(0, connection.PendingRequestCount);
    }

    [Fact]
    public async Task Registry_marks_all_current_connections_draining_and_aborts_them_for_shutdown()
    {
        var registry = NewRegistry();
        using var socketA = new DrainTestWebSocket();
        using var socketB = new DrainTestWebSocket();
        registry.TryRegister("device-a", socketA, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", CancellationToken.None, out var agentA);
        registry.TryRegister("device-b", socketB, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", CancellationToken.None, out var agentB);
        using (agentA)
        using (agentB)
        {
            var pendingA = agentA.SendAsync(Request("a"), TimeSpan.FromSeconds(5), CancellationToken.None);
            var pendingB = agentB.SendAsync(Request("b"), TimeSpan.FromSeconds(5), CancellationToken.None);
            await socketA.WaitForSendAsync();
            await socketB.WaitForSendAsync();
            Assert.Equal(2, registry.TotalPendingRequestCount);

            Assert.Equal(2, registry.BeginShutdownDrain());
            Assert.True(agentA.IsDraining);
            Assert.True(agentB.IsDraining);
            Assert.Equal(503, (await agentA.SendAsync(Request("a-new"), TimeSpan.FromSeconds(1), CancellationToken.None)).StatusCode);
            Assert.Equal(503, (await agentB.SendAsync(Request("b-new"), TimeSpan.FromSeconds(1), CancellationToken.None)).StatusCode);

            Assert.Equal(2, registry.AbortConnectionsForShutdown());
            await Assert.ThrowsAsync<AgentTransportLostException>(() => pendingA);
            await Assert.ThrowsAsync<AgentTransportLostException>(() => pendingB);
            Assert.True(socketA.Aborted);
            Assert.True(socketB.Aborted);
            Assert.Equal(0, registry.TotalPendingRequestCount);
        }
    }

    [Fact]
    public async Task Hosted_service_waits_for_pending_work_then_disconnects_agents()
    {
        var options = Options.Create(new RelayOptions
        {
            AgentReconnectGraceSeconds = 20,
            MaxPendingRequestsPerAgent = 4,
            ShutdownDrainSeconds = 1,
            InternalApiKey = "test-key"
        });
        var registry = new AgentRegistry(
            NullLogger<AgentRegistry>.Instance,
            new RelayInstanceIdentity(),
            options);
        using var socket = new DrainTestWebSocket();
        registry.TryRegister("device", socket, "cccccccccccccccccccccccccccccccc", CancellationToken.None, out var connection);
        using (connection)
        {
            var pending = connection.SendAsync(Request("pending"), TimeSpan.FromSeconds(5), CancellationToken.None);
            await socket.WaitForSendAsync();

            var service = new AgentPresenceLeaseService(
                registry,
                new StubHttpClientFactory(),
                options,
                NullLogger<AgentPresenceLeaseService>.Instance);

            var stopping = service.StopAsync(CancellationToken.None);
            await Task.Delay(30);
            Assert.False(stopping.IsCompleted);
            Assert.True(connection.IsDraining);

            Assert.True(connection.Complete(Response("pending")));
            Assert.Equal("pending", (await pending).Id);

            await stopping.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(socket.Aborted);
            Assert.Equal(0, registry.TotalPendingRequestCount);
        }
    }

    [Fact]
    public async Task Zero_second_drain_aborts_pending_work_immediately()
    {
        var options = Options.Create(new RelayOptions
        {
            AgentReconnectGraceSeconds = 20,
            MaxPendingRequestsPerAgent = 4,
            ShutdownDrainSeconds = 0,
            InternalApiKey = "test-key"
        });
        var registry = new AgentRegistry(
            NullLogger<AgentRegistry>.Instance,
            new RelayInstanceIdentity(),
            options);
        using var socket = new DrainTestWebSocket();
        registry.TryRegister("device", socket, "dddddddddddddddddddddddddddddddd", CancellationToken.None, out var connection);
        using (connection)
        {
            var pending = connection.SendAsync(Request("pending"), TimeSpan.FromSeconds(5), CancellationToken.None);
            await socket.WaitForSendAsync();

            var service = new AgentPresenceLeaseService(
                registry,
                new StubHttpClientFactory(),
                options,
                NullLogger<AgentPresenceLeaseService>.Instance);

            await service.StopAsync(CancellationToken.None);

            await Assert.ThrowsAsync<AgentTransportLostException>(() => pending);
            Assert.True(socket.Aborted);
            Assert.Equal(0, registry.TotalPendingRequestCount);
        }
    }

    private static AgentRegistry NewRegistry()
        => new(
            NullLogger<AgentRegistry>.Instance,
            new RelayInstanceIdentity(),
            Options.Create(new RelayOptions
            {
                AgentReconnectGraceSeconds = 20,
                MaxPendingRequestsPerAgent = 4,
                ShutdownDrainSeconds = 1
            }));

    private static RelayRequest Request(string id)
        => new(id, "POST", "/mcp", new Dictionary<string, string[]>
        {
            ["Mcp-Session-Id"] = [$"session-{id}"]
        }, null, $"operation-{id}");

    private static RelayResponse Response(string id) => new(id, 200, new(), null, null);

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler());
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class DrainTestWebSocket : WebSocket
    {
        private readonly SemaphoreSlim _sendSignals = new(0);
        private WebSocketState _state = WebSocketState.Open;
        private int _sendCount;

        public bool Aborted { get; private set; }
        public int SendCount => Volatile.Read(ref _sendCount);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public async Task WaitForSendAsync()
            => await _sendSignals.WaitAsync(TimeSpan.FromSeconds(2));

        public override void Abort()
        {
            Aborted = true;
            _state = WebSocketState.Aborted;
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
