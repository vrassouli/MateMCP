using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Relay;

public sealed class RelayConnector(IOptionsMonitor<Configuration.MateOptions> options, ILogger<RelayConnector> logger) : BackgroundService
{
    private readonly HttpClient _http = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var current = options.CurrentValue;
            if (!current.Relay.Enabled || string.IsNullOrWhiteSpace(current.Relay.Url) || string.IsNullOrWhiteSpace(current.Relay.DeviceId) || string.IsNullOrWhiteSpace(current.Relay.AgentToken))
            { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); continue; }
            try { await RunConnectionAsync(current, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogWarning(ex, "Relay connection lost; reconnecting."); await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        }
    }

    private async Task RunConnectionAsync(Configuration.MateOptions current, CancellationToken ct)
    {
        var relayUrl = current.Relay.Url!;
        var deviceId = current.Relay.DeviceId!;
        var agentToken = current.Relay.AgentToken!;

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {agentToken}");
        var uri = new Uri($"{relayUrl.TrimEnd('/')}/relay/agent/{Uri.EscapeDataString(deviceId)}".Replace("https://", "wss://").Replace("http://", "ws://"));
        await socket.ConnectAsync(uri, ct);
        logger.LogInformation("Connected to MateMCP Relay as {DeviceId}", deviceId);
        var buffer = new byte[Math.Max(64 * 1024, current.Relay.MaxMessageBytes)];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream(); WebSocketReceiveResult result;
            do { result = await socket.ReceiveAsync(buffer, ct); if (result.MessageType == WebSocketMessageType.Close) return; ms.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
            var request = JsonSerializer.Deserialize<RelayRequest>(ms.ToArray());
            if (request is null) continue;
            var response = await ForwardAsync(request, current, ct);
            await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(response), WebSocketMessageType.Text, true, ct);
        }
    }

    private async Task<RelayResponse> ForwardAsync(RelayRequest request, Configuration.MateOptions current, CancellationToken ct)
    {
        try
        {
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), $"http://127.0.0.1:{current.Port}{request.Path}");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.AccessToken);

            if (request.BodyBase64 is not null)
                message.Content = new ByteArrayContent(Convert.FromBase64String(request.BodyBase64));

            foreach (var h in request.Headers)
            {
                if (message.Headers.TryAddWithoutValidation(h.Key, h.Value))
                    continue;

                message.Content ??= new ByteArrayContent([]);
                message.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            using var upstream = await _http.SendAsync(message, HttpCompletionOption.ResponseContentRead, ct);
            var body = await upstream.Content.ReadAsByteArrayAsync(ct);
            var headers = upstream.Headers.Concat(upstream.Content.Headers).ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            return new RelayResponse(request.Id, (int)upstream.StatusCode, headers, body.Length == 0 ? null : Convert.ToBase64String(body), null);
        }
        catch (Exception ex) { return new RelayResponse(request.Id, 502, new(), null, ex.Message); }
    }

    private sealed record RelayRequest(string Id, string Method, string Path, Dictionary<string,string[]> Headers, string? BodyBase64);
    private sealed record RelayResponse(string Id, int StatusCode, Dictionary<string,string[]> Headers, string? BodyBase64, string? Error);
}
