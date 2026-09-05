using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace MateMCP.Relay;

public sealed class AgentRegistry(ILogger<AgentRegistry> logger, RelayInstanceIdentity instanceIdentity)
{
    private readonly ConcurrentDictionary<string, AgentConnection> _agents = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _agents.Count;

    public bool TryRegister(string deviceId, WebSocket socket, string? requestedConnectionId, CancellationToken connectionLifetime, out AgentConnection connection)
    {
        connection = new AgentConnection(deviceId, socket, NormalizeConnectionId(requestedConnectionId), connectionLifetime);

        while (true)
        {
            if (_agents.TryAdd(deviceId, connection))
            {
                logger.LogInformation(
                    "Relay registry registered Agent: device={DeviceId}; connection={ConnectionId}; instance={RelayInstanceId}; registryCount={RegistryCount}",
                    deviceId, connection.ConnectionId, instanceIdentity.InstanceId, _agents.Count);
                return true;
            }

            if (!_agents.TryGetValue(deviceId, out var existing)) continue;
            if (!_agents.TryUpdate(deviceId, connection, existing)) continue;

            logger.LogWarning(
                "Relay registry replaced Agent connection: device={DeviceId}; oldConnection={OldConnectionId}; newConnection={NewConnectionId}; oldState={OldState}; instance={RelayInstanceId}; registryCount={RegistryCount}",
                deviceId, existing.ConnectionId, connection.ConnectionId, existing.Socket.State, instanceIdentity.InstanceId, _agents.Count);

            existing.Disconnect();
            try { existing.Socket.Abort(); }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "Relay registry could not abort replaced Agent socket: device={DeviceId}; connection={ConnectionId}",
                    deviceId, existing.ConnectionId);
            }
            return true;
        }
    }

    public bool TryGet(string deviceId, out AgentConnection connection) => _agents.TryGetValue(deviceId, out connection!);

    public bool Remove(string deviceId, AgentConnection connection)
    {
        var removed = _agents.TryRemove(new KeyValuePair<string, AgentConnection>(deviceId, connection));
        if (removed)
        {
            logger.LogInformation(
                "Relay registry removed current Agent connection: device={DeviceId}; connection={ConnectionId}; instance={RelayInstanceId}; registryCount={RegistryCount}",
                deviceId, connection.ConnectionId, instanceIdentity.InstanceId, _agents.Count);
            return true;
        }

        _agents.TryGetValue(deviceId, out var current);
        logger.LogInformation(
            "Relay registry ignored stale Agent removal: device={DeviceId}; staleConnection={StaleConnectionId}; currentConnection={CurrentConnectionId}; staleWasCurrent={StaleWasCurrent}; instance={RelayInstanceId}; registryCount={RegistryCount}",
            deviceId, connection.ConnectionId, current?.ConnectionId ?? "none", false, instanceIdentity.InstanceId, _agents.Count);
        return false;
    }

    public RelayRegistrySnapshot Snapshot(string deviceId)
    {
        _agents.TryGetValue(deviceId, out var current);
        return new RelayRegistrySnapshot(_agents.Count, current?.ConnectionId, current?.ConnectedAt, current?.Socket.State);
    }

    private static string NormalizeConnectionId(string? requestedConnectionId)
    {
        if (Guid.TryParseExact(requestedConnectionId, "N", out var parsed))
            return parsed.ToString("N");
        return Guid.NewGuid().ToString("N");
    }
}

public sealed record RelayRegistrySnapshot(int RegistryCount, string? CurrentConnectionId, DateTimeOffset? ConnectedAt, WebSocketState? SocketState);

public sealed class AgentConnection : IDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RelayResponse>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _transportLifetime;
    private int _disposed;

    public AgentConnection(string deviceId, WebSocket socket, string connectionId, CancellationToken connectionLifetime = default)
    {
        DeviceId = deviceId;
        Socket = socket;
        ConnectionId = connectionId;
        ConnectedAt = DateTimeOffset.UtcNow;
        _transportLifetime = CancellationTokenSource.CreateLinkedTokenSource(connectionLifetime);
    }

    public string DeviceId { get; }
    public WebSocket Socket { get; }
    public string ConnectionId { get; }
    public DateTimeOffset ConnectedAt { get; }
    public int PendingRequestCount => _pending.Count;

    public async Task<RelayResponse> SendAsync(RelayRequest request, TimeSpan timeout, CancellationToken requestCancellation)
    {
        var completion = new TaskCompletionSource<RelayResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.Id, completion)) throw new InvalidOperationException("Duplicate relay request id.");

        try
        {
            await _sendLock.WaitAsync(requestCancellation);
            try
            {
                if (Socket.State != WebSocketState.Open)
                    throw new WebSocketException(WebSocketError.InvalidState, $"Agent socket is {Socket.State}.");

                // A single MCP client's cancellation must not cancel the shared Agent WebSocket transport.
                // Only the Agent connection lifetime is allowed to cancel an in-progress socket write.
                var payload = JsonSerializer.SerializeToUtf8Bytes(request, RelayJsonContext.Default.RelayRequest);
                await Socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, _transportLifetime.Token);
            }
            finally
            {
                _sendLock.Release();
            }

            return await completion.Task.WaitAsync(timeout, requestCancellation);
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    public bool Complete(RelayResponse response)
        => _pending.TryGetValue(response.Id, out var completion) && completion.TrySetResult(response);

    public void Disconnect()
    {
        if (!_transportLifetime.IsCancellationRequested)
            _transportLifetime.Cancel();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Disconnect();
        _transportLifetime.Dispose();
        _sendLock.Dispose();
    }
}

public sealed record RelayRequest(string Id, string Method, string Path, Dictionary<string, string[]> Headers, string? BodyBase64);
public sealed record RelayResponse(string Id, int StatusCode, Dictionary<string, string[]> Headers, string? BodyBase64, string? Error);

[System.Text.Json.Serialization.JsonSerializable(typeof(RelayRequest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(RelayResponse))]
internal partial class RelayJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
