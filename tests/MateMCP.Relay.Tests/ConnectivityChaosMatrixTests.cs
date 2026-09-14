using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using AgentRelay = MateMCP.Agent.Relay;
using Relay = MateMCP.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MateMCP.Relay.Tests;

public sealed class ConnectivityChaosMatrixTests
{
    [Fact]
    public async Task Disconnect_before_agent_accepts_then_reconnect_executes_once()
    {
        var operations = NewOperationRegistry();
        var executions = 0;
        var registry = NewRelayRegistry();
        using var firstSocket = new AgentLoopbackWebSocket(
            operations,
            Execute,
            failBeforeDelivery: true);

        registry.TryRegister("device", firstSocket, ConnectionId(1), CancellationToken.None, out var first);
        using (first)
        {
            firstSocket.ResponseSink = first.Complete;
            var request = Request("transport-1", "session-a", "operation-a");

            await Assert.ThrowsAsync<WebSocketException>(() =>
                first.SendAsync(request, TimeSpan.FromSeconds(2), CancellationToken.None));
            Assert.Equal(0, operations.Count);
            Assert.Equal(0, Volatile.Read(ref executions));

            Assert.True(registry.Remove("device", first));
        }

        using var replacementSocket = new AgentLoopbackWebSocket(operations, Execute);
        registry.TryRegister("device", replacementSocket, ConnectionId(2), CancellationToken.None, out var replacement);
        using (replacement)
        {
            replacementSocket.ResponseSink = replacement.Complete;
            var recovered = await replacement.SendAsync(
                Request("transport-2", "session-a", "operation-a"),
                TimeSpan.FromSeconds(2),
                CancellationToken.None);

            Assert.Equal(200, recovered.StatusCode);
            Assert.Equal(1, Volatile.Read(ref executions));
            var delivered = await replacementSocket.WaitForReceivedAsync("transport-2");
            Assert.Equal("session-a", delivered.SessionId);
            Assert.Equal("operation-a", delivered.OperationId);
        }

        Task<AgentRelay.RelayResponse> Execute(AgentRelay.RelayRequest request, CancellationToken _)
        {
            Interlocked.Increment(ref executions);
            return Task.FromResult(AgentResponse(request.Id, "done"));
        }
    }

    [Fact]
    public async Task Lost_response_after_execution_reconnects_and_replays_without_reexecution()
    {
        var operations = NewOperationRegistry();
        var executions = 0;
        var registry = NewRelayRegistry();
        using var firstSocket = new AgentLoopbackWebSocket(operations, Execute, dropResponse: _ => true);

        registry.TryRegister("device", firstSocket, ConnectionId(1), CancellationToken.None, out var first);
        using (first)
        {
            firstSocket.ResponseSink = first.Complete;
            var firstAttempt = first.SendAsync(
                Request("transport-1", "session-a", "stable-operation"),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

            var completedAtAgent = await firstSocket.WaitForResponseAsync("transport-1");
            Assert.Equal(200, completedAtAgent.StatusCode);
            Assert.Equal(1, Volatile.Read(ref executions));
            Assert.False(firstAttempt.IsCompleted);

            Assert.True(registry.Remove("device", first));
            await Assert.ThrowsAsync<AgentTransportLostException>(() => firstAttempt);
        }

        using var replacementSocket = new AgentLoopbackWebSocket(operations, Execute);
        registry.TryRegister("device", replacementSocket, ConnectionId(2), CancellationToken.None, out var replacement);
        using (replacement)
        {
            replacementSocket.ResponseSink = replacement.Complete;
            var recovered = await replacement.SendAsync(
                Request("transport-2", "session-a", "stable-operation"),
                TimeSpan.FromSeconds(2),
                CancellationToken.None);

            Assert.Equal(200, recovered.StatusCode);
            Assert.Equal("cmVzdWx0LWRvbmU=", recovered.BodyBase64);
            Assert.Equal(1, Volatile.Read(ref executions));

            var retried = await replacementSocket.WaitForReceivedAsync("transport-2");
            Assert.Equal("session-a", retried.SessionId);
            Assert.Equal("stable-operation", retried.OperationId);
        }

        Task<AgentRelay.RelayResponse> Execute(AgentRelay.RelayRequest request, CancellationToken _)
        {
            Interlocked.Increment(ref executions);
            return Task.FromResult(AgentResponse(request.Id, "result-done"));
        }
    }

    [Fact]
    public async Task Disconnect_while_running_retry_waits_same_operation_and_executes_once()
    {
        var operations = NewOperationRegistry();
        var executions = 0;
        var started = NewSignal();
        var release = NewSignal();
        var registry = NewRelayRegistry();
        using var firstSocket = new AgentLoopbackWebSocket(operations, Execute);

        registry.TryRegister("device", firstSocket, ConnectionId(1), CancellationToken.None, out var first);
        using (first)
        {
            firstSocket.ResponseSink = first.Complete;
            var firstAttempt = first.SendAsync(
                Request("transport-1", "session-running", "running-operation"),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, Volatile.Read(ref executions));
            Assert.True(registry.Remove("device", first));
            await Assert.ThrowsAsync<AgentTransportLostException>(() => firstAttempt);
        }

        using var replacementSocket = new AgentLoopbackWebSocket(operations, Execute);
        registry.TryRegister("device", replacementSocket, ConnectionId(2), CancellationToken.None, out var replacement);
        using (replacement)
        {
            replacementSocket.ResponseSink = replacement.Complete;
            var recovered = replacement.SendAsync(
                Request("transport-2", "session-running", "running-operation"),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

            await replacementSocket.WaitForReceivedAsync("transport-2");
            Assert.Equal(1, Volatile.Read(ref executions));
            release.TrySetResult();

            var response = await recovered;
            Assert.Equal(200, response.StatusCode);
            Assert.Equal(1, Volatile.Read(ref executions));
        }

        async Task<AgentRelay.RelayResponse> Execute(AgentRelay.RelayRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref executions);
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return AgentResponse(request.Id, "running-complete");
        }
    }

    [Fact]
    public async Task Concurrent_sessions_survive_one_transport_replacement_without_cross_session_execution()
    {
        var operations = NewOperationRegistry();
        var executionsA = 0;
        var executionsB = 0;
        var startedA = NewSignal();
        var startedB = NewSignal();
        var releaseA = NewSignal();
        var releaseB = NewSignal();
        var registry = NewRelayRegistry();
        using var firstSocket = new AgentLoopbackWebSocket(operations, Execute);

        registry.TryRegister("device", firstSocket, ConnectionId(1), CancellationToken.None, out var first);
        Task<Relay.RelayResponse> firstA;
        Task<Relay.RelayResponse> firstB;
        using (first)
        {
            firstSocket.ResponseSink = first.Complete;
            firstA = first.SendAsync(Request("a-1", "session-a", "operation-a"), TimeSpan.FromSeconds(5), CancellationToken.None);
            firstB = first.SendAsync(Request("b-1", "session-b", "operation-b"), TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.WhenAll(
                startedA.Task.WaitAsync(TimeSpan.FromSeconds(2)),
                startedB.Task.WaitAsync(TimeSpan.FromSeconds(2)));

            Assert.Equal(1, executionsA);
            Assert.Equal(1, executionsB);
            Assert.True(registry.Remove("device", first));
            await Assert.ThrowsAsync<AgentTransportLostException>(() => firstA);
            await Assert.ThrowsAsync<AgentTransportLostException>(() => firstB);
        }

        using var replacementSocket = new AgentLoopbackWebSocket(operations, Execute);
        registry.TryRegister("device", replacementSocket, ConnectionId(2), CancellationToken.None, out var replacement);
        using (replacement)
        {
            replacementSocket.ResponseSink = replacement.Complete;
            var recoveredA = replacement.SendAsync(Request("a-2", "session-a", "operation-a"), TimeSpan.FromSeconds(5), CancellationToken.None);
            var recoveredB = replacement.SendAsync(Request("b-2", "session-b", "operation-b"), TimeSpan.FromSeconds(5), CancellationToken.None);

            var deliveredA = await replacementSocket.WaitForReceivedAsync("a-2");
            var deliveredB = await replacementSocket.WaitForReceivedAsync("b-2");
            Assert.Equal("session-a", deliveredA.SessionId);
            Assert.Equal("session-b", deliveredB.SessionId);
            Assert.Equal("operation-a", deliveredA.OperationId);
            Assert.Equal("operation-b", deliveredB.OperationId);
            Assert.Equal(1, executionsA);
            Assert.Equal(1, executionsB);

            releaseB.TrySetResult();
            Assert.Equal(200, (await recoveredB).StatusCode);
            Assert.False(recoveredA.IsCompleted);
            releaseA.TrySetResult();
            Assert.Equal(200, (await recoveredA).StatusCode);

            Assert.Equal(1, executionsA);
            Assert.Equal(1, executionsB);
        }

        async Task<AgentRelay.RelayResponse> Execute(AgentRelay.RelayRequest request, CancellationToken ct)
        {
            if (request.OperationId == "operation-a")
            {
                Interlocked.Increment(ref executionsA);
                startedA.TrySetResult();
                await releaseA.Task.WaitAsync(ct);
                return AgentResponse(request.Id, "a-complete");
            }

            Interlocked.Increment(ref executionsB);
            startedB.TrySetResult();
            await releaseB.Task.WaitAsync(ct);
            return AgentResponse(request.Id, "b-complete");
        }
    }

    private static AgentRelay.RelayOperationRegistry NewOperationRegistry()
        => new(NullLogger<AgentRelay.RelayOperationRegistry>.Instance, 64, TimeSpan.FromMinutes(5), () => DateTimeOffset.UtcNow);

    private static Relay.AgentRegistry NewRelayRegistry(int graceSeconds = 20)
        => new(
            NullLogger<Relay.AgentRegistry>.Instance,
            new Relay.RelayInstanceIdentity(),
            Options.Create(new Relay.RelayOptions
            {
                AgentReconnectGraceSeconds = graceSeconds,
                MaxPendingRequestsPerAgent = 8
            }));

    private static Relay.RelayRequest Request(string transportId, string sessionId, string operationId)
        => new(
            transportId,
            "POST",
            "/mcp",
            new Dictionary<string, string[]> { ["Mcp-Session-Id"] = [sessionId] },
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("same-payload")),
            operationId);

    private static AgentRelay.RelayResponse AgentResponse(string id, string value)
        => new(id, 200, new Dictionary<string, string[]>(), Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value)), null);

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string ConnectionId(int generation)
        => generation.ToString("x32");

    private sealed class AgentLoopbackWebSocket : WebSocket
    {
        private readonly AgentRelay.RelayOperationRegistry _operations;
        private readonly Func<AgentRelay.RelayRequest, CancellationToken, Task<AgentRelay.RelayResponse>> _execute;
        private readonly Func<AgentRelay.RelayRequest, bool> _dropResponse;
        private readonly bool _failBeforeDelivery;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<AgentRelay.RelayRequest>> _received = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<AgentRelay.RelayResponse>> _responses = new(StringComparer.Ordinal);
        private WebSocketState _state = WebSocketState.Open;

        public AgentLoopbackWebSocket(
            AgentRelay.RelayOperationRegistry operations,
            Func<AgentRelay.RelayRequest, CancellationToken, Task<AgentRelay.RelayResponse>> execute,
            Func<AgentRelay.RelayRequest, bool>? dropResponse = null,
            bool failBeforeDelivery = false)
        {
            _operations = operations;
            _execute = execute;
            _dropResponse = dropResponse ?? (_ => false);
            _failBeforeDelivery = failBeforeDelivery;
        }

        public Func<Relay.RelayResponse, bool>? ResponseSink { get; set; }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public Task<AgentRelay.RelayRequest> WaitForReceivedAsync(string requestId)
            => Signal(_received, requestId).Task.WaitAsync(TimeSpan.FromSeconds(2));

        public Task<AgentRelay.RelayResponse> WaitForResponseAsync(string requestId)
            => Signal(_responses, requestId).Task.WaitAsync(TimeSpan.FromSeconds(2));

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
            if (_failBeforeDelivery)
                throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);

            var payload = buffer.Array!.AsSpan(buffer.Offset, buffer.Count).ToArray();
            _ = ProcessAsync(payload, cancellationToken);
            return Task.CompletedTask;
        }

        private async Task ProcessAsync(byte[] payload, CancellationToken transportCancellation)
        {
            AgentRelay.RelayRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<AgentRelay.RelayRequest>(payload)
                          ?? throw new InvalidOperationException("Relay request payload could not be deserialized by the Agent model.");
                Signal(_received, request.Id).TrySetResult(request);

                var response = await _operations.ExecuteAsync(
                    request,
                    ct => _execute(request, ct),
                    CancellationToken.None,
                    transportCancellation);
                Signal(_responses, request.Id).TrySetResult(response);

                if (_dropResponse(request)) return;
                ResponseSink?.Invoke(new Relay.RelayResponse(
                    response.Id,
                    response.StatusCode,
                    response.Headers.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                    response.BodyBase64,
                    response.Error));
            }
            catch (OperationCanceledException) when (transportCancellation.IsCancellationRequested)
            {
                if (request is not null) Signal(_responses, request.Id).TrySetCanceled(transportCancellation);
            }
            catch (Exception ex)
            {
                if (request is not null) Signal(_responses, request.Id).TrySetException(ex);
            }
        }

        private static TaskCompletionSource<T> Signal<T>(
            ConcurrentDictionary<string, TaskCompletionSource<T>> signals,
            string id)
            => signals.GetOrAdd(id, static _ => new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
