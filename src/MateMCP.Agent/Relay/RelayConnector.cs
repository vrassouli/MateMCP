using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MateMCP.Agent.Security;

namespace MateMCP.Agent.Relay;

public sealed class RelayConnector(IOptionsMonitor<Configuration.MateOptions> options, AgentCredentialStore credentials, LocalAccessCredential localAccess, ILogger<RelayConnector> logger) : BackgroundService
{
    private readonly HttpClient _http = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reconnectAttempt = 0;
        DateTimeOffset? offlineSince = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            var current = options.CurrentValue;
            if (!current.Relay.Enabled || string.IsNullOrWhiteSpace(current.Relay.Url) || string.IsNullOrWhiteSpace(current.Relay.DeviceId))
            {
                reconnectAttempt = 0;
                offlineSince = null;
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            var connectionId = Guid.NewGuid().ToString("N");
            var attempt = reconnectAttempt + 1;
            logger.LogInformation(
                "Relay connect attempt: device={DeviceId}; agentConnection={ConnectionId}; attempt={ReconnectAttempt}; relay={RelayUrl}",
                current.Relay.DeviceId,
                connectionId,
                attempt,
                current.Relay.Url);

            try
            {
                await RunConnectionAsync(current, connectionId, attempt, offlineSince, () =>
                {
                    offlineSince = null;
                    reconnectAttempt = 0;
                }, stoppingToken);
                if (stoppingToken.IsCancellationRequested) break;

                offlineSince ??= DateTimeOffset.UtcNow;
                reconnectAttempt++;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                offlineSince ??= DateTimeOffset.UtcNow;
                reconnectAttempt++;
                logger.LogWarning(ex,
                    "Relay connection lost; reconnecting: device={DeviceId}; agentConnection={ConnectionId}; attempt={ReconnectAttempt}; exceptionType={ExceptionType}; exceptionMessage={ExceptionMessage}",
                    current.Relay.DeviceId,
                    connectionId,
                    attempt,
                    ex.GetType().Name,
                    ex.Message);
            }

            if (!stoppingToken.IsCancellationRequested)
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    private async Task RunConnectionAsync(
        Configuration.MateOptions current,
        string connectionId,
        int reconnectAttempt,
        DateTimeOffset? offlineSince,
        Action onConnected,
        CancellationToken ct)
    {
        var relayUrl = current.Relay.Url!;
        var deviceId = current.Relay.DeviceId!;
        var agentToken = await credentials.GetAsync(deviceId, ct) ?? throw new InvalidOperationException("Agent credential is missing from secure storage. Re-enroll this Agent.");

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {agentToken}");
        socket.Options.SetRequestHeader("X-MateMCP-Agent-Connection-Id", connectionId);
        var uri = new Uri($"{relayUrl.TrimEnd('/')}/relay/agent/{Uri.EscapeDataString(deviceId)}".Replace("https://", "wss://").Replace("http://", "ws://"));

        await socket.ConnectAsync(uri, ct);
        var connectedAt = DateTimeOffset.UtcNow;
        var offlineGap = offlineSince is null ? TimeSpan.Zero : connectedAt - offlineSince.Value;
        logger.LogInformation(
            "Connected to MateMCP Relay: device={DeviceId}; agentConnection={ConnectionId}; reconnectAttempt={ReconnectAttempt}; connectedAt={ConnectedAt:O}; elapsedOfflineMs={ElapsedOfflineMs:F0}",
            deviceId,
            connectionId,
            reconnectAttempt,
            connectedAt,
            Math.Max(0, offlineGap.TotalMilliseconds));
        onConnected();

        if (reconnectAttempt > 1)
        {
            logger.LogInformation(
                "Relay reconnect succeeded: device={DeviceId}; agentConnection={ConnectionId}; reconnectAttempt={ReconnectAttempt}; elapsedOfflineMs={ElapsedOfflineMs:F0}",
                deviceId, connectionId, reconnectAttempt, Math.Max(0, offlineGap.TotalMilliseconds));
        }

        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? transportFailure = null;
        var failureGate = new object();
        var maxConcurrency = Math.Clamp(current.Relay.MaxConcurrentRequests, 1, 64);

        await using var scheduler = new RelayRequestScheduler(
            maxConcurrency,
            (request, workerToken) => ForwardAsync(request, current, workerToken),
            async (response, sendToken) =>
            {
                var payload = JsonSerializer.SerializeToUtf8Bytes(response);
                await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, sendToken);
            },
            connectionCts.Token,
            ex =>
            {
                lock (failureGate)
                {
                    transportFailure ??= ex;
                }
                try { socket.Abort(); } catch { }
                connectionCts.Cancel();
            });

        var buffer = new byte[Math.Max(64 * 1024, current.Relay.MaxMessageBytes)];
        WebSocketCloseStatus? closeStatus = null;
        string? closeReason = null;
        Exception? disconnectException = null;

        try
        {
            while (socket.State == WebSocketState.Open && !connectionCts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), connectionCts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        closeStatus = result.CloseStatus;
                        closeReason = SafeReason(result.CloseStatusDescription);
                        break;
                    }

                    ms.Write(buffer, 0, result.Count);
                    if (ms.Length > current.Relay.MaxMessageBytes)
                        throw new InvalidOperationException("Relay request exceeded the configured maximum message size.");
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close) break;

                RelayRequest? request;
                try
                {
                    request = JsonSerializer.Deserialize<RelayRequest>(ms.ToArray());
                }
                catch (JsonException ex)
                {
                    logger.LogWarning(ex,
                        "Ignored malformed Relay request: device={DeviceId}; agentConnection={ConnectionId}; exceptionType={ExceptionType}",
                        deviceId,
                        connectionId,
                        ex.GetType().Name);
                    continue;
                }

                if (request is null) continue;
                if (!scheduler.TryQueue(request))
                {
                    logger.LogWarning(
                        "Ignored duplicate or shutdown Relay request: device={DeviceId}; agentConnection={ConnectionId}; relayRequestId={RelayRequestId}; inFlight={InFlightCount}",
                        deviceId,
                        connectionId,
                        request.Id,
                        scheduler.InFlightCount);
                }
            }

            lock (failureGate)
            {
                if (transportFailure is not null)
                    throw new IOException("Relay WebSocket response transport failed.", transportFailure);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException ex) when (connectionCts.IsCancellationRequested)
        {
            lock (failureGate)
            {
                if (transportFailure is not null)
                {
                    disconnectException = transportFailure;
                    throw new IOException("Relay WebSocket response transport failed.", transportFailure);
                }
            }
            disconnectException = ex;
            throw;
        }
        catch (Exception ex)
        {
            disconnectException = ex;
            throw;
        }
        finally
        {
            connectionCts.Cancel();
            await scheduler.DrainAsync();
            var disconnectedAt = DateTimeOffset.UtcNow;
            logger.LogWarning(
                "Disconnected from MateMCP Relay: device={DeviceId}; agentConnection={ConnectionId}; connectedAt={ConnectedAt:O}; disconnectedAt={DisconnectedAt:O}; lifetimeMs={LifetimeMs:F0}; socketState={SocketState}; closeStatus={CloseStatus}; closeReason={CloseReason}; exceptionType={ExceptionType}; exceptionMessage={ExceptionMessage}; reconnectAttempt={ReconnectAttempt}",
                deviceId,
                connectionId,
                connectedAt,
                disconnectedAt,
                (disconnectedAt - connectedAt).TotalMilliseconds,
                socket.State,
                closeStatus?.ToString() ?? socket.CloseStatus?.ToString() ?? "none",
                closeReason ?? SafeReason(socket.CloseStatusDescription) ?? "none",
                disconnectException?.GetType().Name ?? "none",
                disconnectException?.Message ?? "none",
                reconnectAttempt);
        }
    }

    private async Task<RelayResponse> ForwardAsync(RelayRequest request, Configuration.MateOptions current, CancellationToken ct)
    {
        try
        {
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), $"http://127.0.0.1:{current.Port}{request.Path}");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", localAccess.Token);

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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new RelayResponse(request.Id, 502, new(), null, ex.Message);
        }
    }

    private static string? SafeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var safe = reason.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return safe.Length <= 160 ? safe : safe[..160];
    }
}
