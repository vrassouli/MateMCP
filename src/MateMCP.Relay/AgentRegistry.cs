using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MateMCP.Relay;

public enum AgentPresenceState
{
    Online,
    Reconnecting
}

public sealed class AgentRegistry
{
    private readonly ILogger<AgentRegistry> _logger;
    private readonly RelayInstanceIdentity _instanceIdentity;
    private readonly TimeSpan _reconnectGrace;
    private readonly ConcurrentDictionary<string, AgentSession> _agents = new(StringComparer.OrdinalIgnoreCase);

    public AgentRegistry(ILogger<AgentRegistry> logger, RelayInstanceIdentity instanceIdentity, IOptions<RelayOptions> options)
    {
        _logger = logger;
        _instanceIdentity = instanceIdentity;
        _reconnectGrace = TimeSpan.FromSeconds(Math.Clamp(options.Value.AgentReconnectGraceSeconds, 0, 300));
    }

    public int Count => _agents.Count;

    public bool TryRegister(string deviceId, WebSocket socket, string? requestedConnectionId, CancellationToken connectionLifetime, out AgentConnection connection)
    {
        connection = new AgentConnection(deviceId, socket, NormalizeConnectionId(requestedConnectionId), connectionLifetime);

        while (true)
        {
            var session = _agents.GetOrAdd(deviceId, static id => new AgentSession(id));
            AgentConnection? existing;
            bool resumed;

            lock (session.Gate)
            {
                if (!_agents.TryGetValue(deviceId, out var currentSession) || !ReferenceEquals(currentSession, session))
                    continue;

                existing = session.Connection;
                resumed = session.State == AgentPresenceState.Reconnecting;
                session.Connection = connection;
                session.State = AgentPresenceState.Online;
                session.LastDisconnectedAt = null;
                session.ReconnectUntil = null;
            }

            if (existing is not null && !ReferenceEquals(existing, connection))
            {
                _logger.LogWarning(
                    "Relay registry replaced Agent connection: device={DeviceId}; oldConnection={OldConnectionId}; newConnection={NewConnectionId}; oldState={OldState}; instance={RelayInstanceId}; registryCount={RegistryCount}",
                    deviceId, existing.ConnectionId, connection.ConnectionId, existing.Socket.State, _instanceIdentity.InstanceId, _agents.Count);

                existing.Disconnect();
                try { existing.Socket.Abort(); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "Relay registry could not abort replaced Agent socket: device={DeviceId}; connection={ConnectionId}",
                        deviceId, existing.ConnectionId);
                }
            }
            else if (resumed)
            {
                _logger.LogInformation(
                    "Relay registry rebound reconnecting Agent: device={DeviceId}; connection={ConnectionId}; instance={RelayInstanceId}; registryCount={RegistryCount}",
                    deviceId, connection.ConnectionId, _instanceIdentity.InstanceId, _agents.Count);
            }
            else
            {
                _logger.LogInformation(
                    "Relay registry registered Agent: device={DeviceId}; connection={ConnectionId}; instance={RelayInstanceId}; registryCount={RegistryCount}",
                    deviceId, connection.ConnectionId, _instanceIdentity.InstanceId, _agents.Count);
            }

            return true;
        }
    }

    public bool TryGet(string deviceId, out AgentConnection connection)
    {
        connection = null!;
        if (!_agents.TryGetValue(deviceId, out var session)) return false;

        lock (session.Gate)
        {
            if (session.State != AgentPresenceState.Online || session.Connection is null)
                return false;

            connection = session.Connection;
            return true;
        }
    }

    public bool Remove(string deviceId, AgentConnection connection)
    {
        if (!_agents.TryGetValue(deviceId, out var session))
        {
            LogStaleRemoval(deviceId, connection, null);
            return false;
        }

        DateTimeOffset disconnectedAt;
        DateTimeOffset reconnectUntil;

        lock (session.Gate)
        {
            if (!_agents.TryGetValue(deviceId, out var currentSession) ||
                !ReferenceEquals(currentSession, session) ||
                !ReferenceEquals(session.Connection, connection))
            {
                LogStaleRemoval(deviceId, connection, session.Connection);
                return false;
            }

            connection.Disconnect();
            disconnectedAt = DateTimeOffset.UtcNow;
            reconnectUntil = disconnectedAt + _reconnectGrace;
            session.Connection = null;
            session.State = AgentPresenceState.Reconnecting;
            session.LastDisconnectedAt = disconnectedAt;
            session.ReconnectUntil = reconnectUntil;
        }

        _logger.LogWarning(
            "Relay registry entered reconnect grace: device={DeviceId}; connection={ConnectionId}; disconnectedAt={DisconnectedAt:O}; reconnectUntil={ReconnectUntil:O}; graceMs={ReconnectGraceMs:F0}; instance={RelayInstanceId}; registryCount={RegistryCount}",
            deviceId, connection.ConnectionId, disconnectedAt, reconnectUntil, _reconnectGrace.TotalMilliseconds, _instanceIdentity.InstanceId, _agents.Count);
        return true;
    }

    public IReadOnlyList<ExpiredAgentPresence> ExpireReconnects(DateTimeOffset now)
    {
        var expired = new List<ExpiredAgentPresence>();

        foreach (var pair in _agents)
        {
            var session = pair.Value;
            ExpiredAgentPresence? expiration = null;

            lock (session.Gate)
            {
                if (session.State != AgentPresenceState.Reconnecting ||
                    session.Connection is not null ||
                    session.ReconnectUntil is null ||
                    session.ReconnectUntil > now ||
                    session.LastDisconnectedAt is null)
                {
                    continue;
                }

                if (!_agents.TryRemove(new KeyValuePair<string, AgentSession>(pair.Key, session)))
                    continue;

                expiration = new ExpiredAgentPresence(pair.Key, session.LastDisconnectedAt.Value, session.ReconnectUntil.Value);
            }

            if (expiration is not null)
            {
                expired.Add(expiration);
                _logger.LogWarning(
                    "Relay registry reconnect lease expired: device={DeviceId}; disconnectedAt={DisconnectedAt:O}; reconnectUntil={ReconnectUntil:O}; expiredAt={ExpiredAt:O}; instance={RelayInstanceId}; registryCount={RegistryCount}",
                    expiration.DeviceId, expiration.LastDisconnectedAt, expiration.ReconnectUntil, now, _instanceIdentity.InstanceId, _agents.Count);
            }
        }

        return expired;
    }

    public RelayRegistrySnapshot Snapshot(string deviceId)
    {
        if (!_agents.TryGetValue(deviceId, out var session))
            return new RelayRegistrySnapshot(_agents.Count, null, null, null, null, null, null);

        lock (session.Gate)
        {
            return new RelayRegistrySnapshot(
                _agents.Count,
                session.Connection?.ConnectionId,
                session.Connection?.ConnectedAt,
                session.Connection?.Socket.State,
                session.State,
                session.LastDisconnectedAt,
                session.ReconnectUntil);
        }
    }

    private void LogStaleRemoval(string deviceId, AgentConnection stale, AgentConnection? current)
    {
        _logger.LogInformation(
            "Relay registry ignored stale Agent removal: device={DeviceId}; staleConnection={StaleConnectionId}; currentConnection={CurrentConnectionId}; staleWasCurrent={StaleWasCurrent}; instance={RelayInstanceId}; registryCount={RegistryCount}",
            deviceId, stale.ConnectionId, current?.ConnectionId ?? "none", false, _instanceIdentity.InstanceId, _agents.Count);
    }

    private static string NormalizeConnectionId(string? requestedConnectionId)
    {
        if (Guid.TryParseExact(requestedConnectionId, "N", out var parsed))
            return parsed.ToString("N");
        return Guid.NewGuid().ToString("N");
    }

    private sealed class AgentSession(string deviceId)
    {
        public object Gate { get; } = new();
        public string DeviceId { get; } = deviceId;
        public AgentConnection? Connection { get; set; }
        public AgentPresenceState State { get; set; } = AgentPresenceState.Reconnecting;
        public DateTimeOffset? LastDisconnectedAt { get; set; }
        public DateTimeOffset? ReconnectUntil { get; set; }
    }
}

public sealed record RelayRegistrySnapshot(
    int RegistryCount,
    string? CurrentConnectionId,
    DateTimeOffset? ConnectedAt,
    WebSocketState? SocketState,
    AgentPresenceState? State,
    DateTimeOffset? LastDisconnectedAt,
    DateTimeOffset? ReconnectUntil)
{
    public bool IsReconnectGraceActive(DateTimeOffset now)
        => State == AgentPresenceState.Reconnecting && ReconnectUntil is not null && ReconnectUntil > now;
}

public sealed record ExpiredAgentPresence(string DeviceId, DateTimeOffset LastDisconnectedAt, DateTimeOffset ReconnectUntil);

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
