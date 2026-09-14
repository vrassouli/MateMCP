using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace MateMCP.Relay;

public sealed class AgentPresenceLeaseService(
    AgentRegistry registry,
    IHttpClientFactory clients,
    IOptions<RelayOptions> options,
    ILogger<AgentPresenceLeaseService> logger) : BackgroundService
{
    private readonly RelayOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var expired = registry.ExpireReconnects(DateTimeOffset.UtcNow);
                foreach (var presence in expired)
                    await MarkAgentOfflineAsync(presence, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task MarkAgentOfflineAsync(ExpiredAgentPresence presence, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            var client = clients.CreateClient("control-plane");
            using var request = new HttpRequestMessage(HttpMethod.Post, "internal/agents/offline")
            {
                Content = JsonContent.Create(new { agentId = presence.DeviceId, lastSeenAt = presence.LastDisconnectedAt })
            };
            request.Headers.Add("X-MateMCP-Internal-Key", _options.InternalApiKey);
            using var response = await client.SendAsync(request, timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Control plane rejected expired Agent presence update: device={DeviceId}; status={StatusCode}; disconnectedAt={DisconnectedAt:O}; reconnectUntil={ReconnectUntil:O}",
                    presence.DeviceId, (int)response.StatusCode, presence.LastDisconnectedAt, presence.ReconnectUntil);
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Timed out marking expired Agent presence offline: device={DeviceId}; disconnectedAt={DisconnectedAt:O}",
                presence.DeviceId, presence.LastDisconnectedAt);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex,
                "Failed marking expired Agent presence offline: device={DeviceId}; disconnectedAt={DisconnectedAt:O}",
                presence.DeviceId, presence.LastDisconnectedAt);
        }
    }
}
