using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace MateMCP.Relay;

public sealed class AgentRegistry
{
    private readonly ConcurrentDictionary<string, AgentConnection> _agents = new(StringComparer.OrdinalIgnoreCase);

    public bool TryRegister(string deviceId, WebSocket socket, out AgentConnection connection)
    {
        connection = new AgentConnection(deviceId, socket);

        while (true)
        {
            if (_agents.TryAdd(deviceId, connection)) return true;
            if (!_agents.TryGetValue(deviceId, out var existing)) continue;

            if (!_agents.TryUpdate(deviceId, connection, existing)) continue;

            try { existing.Socket.Abort(); }
            catch { }
            return true;
        }
    }

    public bool TryGet(string deviceId, out AgentConnection connection) => _agents.TryGetValue(deviceId, out connection!);

    public bool Remove(string deviceId, AgentConnection connection) => _agents.TryRemove(new KeyValuePair<string, AgentConnection>(deviceId, connection));
}

public sealed class AgentConnection(string deviceId, WebSocket socket)
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RelayResponse>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    public string DeviceId { get; } = deviceId;
    public WebSocket Socket { get; } = socket;

    public async Task<RelayResponse> SendAsync(RelayRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<RelayResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.Id, completion)) throw new InvalidOperationException("Duplicate relay request id.");
        try
        {
            await _sendLock.WaitAsync(cancellationToken);
            try { await Socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(request, RelayJsonContext.Default.RelayRequest), WebSocketMessageType.Text, true, cancellationToken); }
            finally { _sendLock.Release(); }
            return await completion.Task.WaitAsync(timeout, cancellationToken);
        }
        finally { _pending.TryRemove(request.Id, out _); }
    }

    public void Complete(RelayResponse response)
    {
        if (_pending.TryGetValue(response.Id, out var completion)) completion.TrySetResult(response);
    }
}

public sealed record RelayRequest(string Id, string Method, string Path, Dictionary<string,string[]> Headers, string? BodyBase64);
public sealed record RelayResponse(string Id, int StatusCode, Dictionary<string,string[]> Headers, string? BodyBase64, string? Error);

[System.Text.Json.Serialization.JsonSerializable(typeof(RelayRequest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(RelayResponse))]
internal partial class RelayJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
