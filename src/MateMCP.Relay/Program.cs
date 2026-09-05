using System.Diagnostics;
using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using MateMCP.Relay;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection(RelayOptions.SectionName));
builder.Services.AddSingleton<RelayInstanceIdentity>();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddHttpClient("control-plane", client => client.BaseAddress = new Uri((builder.Configuration["Relay:ControlPlaneUrl"] ?? "https://api.matemcp.com").TrimEnd('/') + "/"));

var options = builder.Configuration.GetSection(RelayOptions.SectionName).Get<RelayOptions>() ?? new RelayOptions();
if (options.InternalApiKey == "change-me")
    throw new InvalidOperationException("Configure Relay:InternalApiKey.");

var publicBaseUrl = options.PublicBaseUrl.TrimEnd('/');
var authorizationServerIssuer = OAuthDiscovery.NormalizeIssuer(options.AuthorizationServerUrl);

builder.Services.AddOpenIddict().AddValidation(validation =>
{
    validation.SetIssuer(new Uri(authorizationServerIssuer));
    validation.UseSystemNetHttp();
    validation.UseAspNetCore();
});

var app = builder.Build();
var logger = app.Logger;
var relayInstance = app.Services.GetRequiredService<RelayInstanceIdentity>();
logger.LogInformation(
    "MateMCP Relay starting: instance={RelayInstanceId}; processId={ProcessId}; startedAt={StartedAt:O}",
    relayInstance.InstanceId,
    Environment.ProcessId,
    relayInstance.StartedAt);
logger.LogWarning(
    "Relay AgentRegistry is process-local; run a single Relay instance unless distributed Agent connection ownership/routing is implemented. instance={RelayInstanceId}",
    relayInstance.InstanceId);

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20),
    KeepAliveTimeout = TimeSpan.FromSeconds(20)
});

app.Use(async (context, next) =>
{
    var correlationId = context.TraceIdentifier;
    context.Response.Headers["X-MateMCP-Request-Id"] = correlationId;
    context.Response.Headers["X-MateMCP-Relay-Instance"] = relayInstance.InstanceId;
    var started = Stopwatch.GetTimestamp();
    try
    {
        await next();
    }
    finally
    {
        if (context.Request.Path.StartsWithSegments("/mcp") || context.Request.Path.StartsWithSegments("/.well-known"))
        {
            logger.LogInformation(
                "Remote MCP request {RequestId}: stage={Stage}; {Method} {Path} -> {StatusCode} in {ElapsedMs:F1} ms; accept={Accept}; contentType={ContentType}; protocol={ProtocolVersion}; auth={HasAuthorization}; redirect={Redirect}; relayInstance={RelayInstanceId}",
                correlationId,
                McpRequestDiagnostics.Stage(context.Request.Path),
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                McpRequestDiagnostics.SafeHeader(context.Request.Headers.Accept.ToString()),
                McpRequestDiagnostics.SafeHeader(context.Request.ContentType),
                McpRequestDiagnostics.SafeHeader(context.Request.Headers["MCP-Protocol-Version"].ToString()),
                context.Request.Headers.ContainsKey("Authorization"),
                McpRequestDiagnostics.SafeRedirect(context.Response.Headers.Location.ToString()),
                relayInstance.InstanceId);
        }
    }
});

app.MapGet("/health", () => Results.Ok(new
{
    service = "MateMCP.Relay",
    status = "ok",
    instanceId = relayInstance.InstanceId,
    processId = Environment.ProcessId,
    startedAt = relayInstance.StartedAt
}));

app.MapGet("/.well-known/oauth-protected-resource", () => Results.Ok(new
{
    resource = publicBaseUrl,
    authorization_servers = new[] { authorizationServerIssuer },
    scopes_supported = options.OAuthScopes,
    bearer_methods_supported = new[] { "header" },
    resource_documentation = "https://github.com/vrassouli/MateMCP"
}));

app.MapGet("/.well-known/oauth-protected-resource/mcp/{deviceId}", (string deviceId) => Results.Ok(new
{
    resource = $"{publicBaseUrl}/mcp/{deviceId}",
    authorization_servers = new[] { authorizationServerIssuer },
    scopes_supported = options.OAuthScopes,
    bearer_methods_supported = new[] { "header" }
}));

app.Map("/relay/agent/{deviceId}", async (HttpContext context, string deviceId, AgentRegistry registry, IHttpClientFactory clients) =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    var credential = Bearer(context);
    if (credential is null || !await AuthenticateAgentAsync(clients, options, deviceId, credential, context.RequestAborted)) { context.Response.StatusCode = 401; return; }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var requestedConnectionId = context.Request.Headers["X-MateMCP-Agent-Connection-Id"].ToString();
    if (!registry.TryRegister(deviceId, socket, requestedConnectionId, context.RequestAborted, out var connection))
    {
        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "device already connected", context.RequestAborted);
        return;
    }

    logger.LogInformation(
        "Agent relay WebSocket connected: device={DeviceId}; connection={ConnectionId}; connectedAt={ConnectedAt:O}; socketState={SocketState}; relayInstance={RelayInstanceId}",
        deviceId,
        connection.ConnectionId,
        connection.ConnectedAt,
        socket.State,
        relayInstance.InstanceId);

    using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var heartbeatTask = RunAgentHeartbeatAsync(clients, options, deviceId, credential, heartbeatCts.Token);
    var buffer = new byte[64 * 1024];
    WebSocketCloseStatus? closeStatus = null;
    string? closeReason = null;
    Exception? disconnectException = null;

    try
    {
        while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    closeStatus = result.CloseStatus;
                    closeReason = McpRequestDiagnostics.SafeHeader(result.CloseStatusDescription);
                    break;
                }
                ms.Write(buffer, 0, result.Count);
                if (ms.Length > options.MaxBodyBytes * 2L) throw new InvalidOperationException("Relay response too large.");
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close) break;

            var response = JsonSerializer.Deserialize(ms.ToArray(), RelayJsonContext.Default.RelayResponse);
            if (response is not null && !connection.Complete(response))
            {
                logger.LogInformation(
                    "Relay received a late/unmatched Agent response: device={DeviceId}; connection={ConnectionId}; relayRequestId={RelayRequestId}; pending={PendingCount}; relayInstance={RelayInstanceId}",
                    deviceId,
                    connection.ConnectionId,
                    response.Id,
                    connection.PendingRequestCount,
                    relayInstance.InstanceId);
            }
        }
    }
    catch (OperationCanceledException ex) when (context.RequestAborted.IsCancellationRequested)
    {
        disconnectException = ex;
    }
    catch (Exception ex)
    {
        disconnectException = ex;
        logger.LogWarning(ex,
            "Agent relay WebSocket receive loop failed: device={DeviceId}; connection={ConnectionId}; socketState={SocketState}; relayInstance={RelayInstanceId}",
            deviceId,
            connection.ConnectionId,
            socket.State,
            relayInstance.InstanceId);
    }
    finally
    {
        heartbeatCts.Cancel();
        try { await heartbeatTask; }
        catch (OperationCanceledException) when (heartbeatCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "Agent heartbeat task ended with an error during disconnect: device={DeviceId}; connection={ConnectionId}",
                deviceId,
                connection.ConnectionId);
        }

        connection.Disconnect();
        var removedCurrent = registry.Remove(deviceId, connection);
        var disconnectedAt = DateTimeOffset.UtcNow;
        logger.LogWarning(
            "Agent relay WebSocket disconnected: device={DeviceId}; connection={ConnectionId}; connectedAt={ConnectedAt:O}; disconnectedAt={DisconnectedAt:O}; lifetimeMs={LifetimeMs:F0}; socketState={SocketState}; closeStatus={CloseStatus}; closeReason={CloseReason}; exceptionType={ExceptionType}; exceptionMessage={ExceptionMessage}; removedCurrent={RemovedCurrent}; relayInstance={RelayInstanceId}",
            deviceId,
            connection.ConnectionId,
            connection.ConnectedAt,
            disconnectedAt,
            (disconnectedAt - connection.ConnectedAt).TotalMilliseconds,
            socket.State,
            closeStatus?.ToString() ?? socket.CloseStatus?.ToString() ?? "none",
            string.IsNullOrWhiteSpace(closeReason) ? McpRequestDiagnostics.SafeHeader(socket.CloseStatusDescription) : closeReason,
            disconnectException?.GetType().Name ?? "none",
            disconnectException?.Message ?? "none",
            removedCurrent,
            relayInstance.InstanceId);

        if (removedCurrent)
            await MarkAgentOfflineAsync(clients, options, deviceId, disconnectedAt);
    }
});

app.MapMethods("/mcp/{deviceId}", ["GET", "HEAD", "POST", "DELETE", "PUT", "PATCH", "OPTIONS"], async (HttpContext context, string deviceId, AgentRegistry registry, IHttpClientFactory clients) =>
{
    if (HttpMethods.IsOptions(context.Request.Method))
    {
        context.Response.Headers["Allow"] = "GET, HEAD, POST, DELETE, PUT, PATCH, OPTIONS";
        return Results.NoContent();
    }

    var principal = await AuthenticateOAuthAsync(context);
    var requiredResource = $"{publicBaseUrl}/mcp/{deviceId}";
    if (principal is null || principal.FindFirstValue("agent_id") != deviceId || !principal.GetAudiences().Contains(requiredResource, StringComparer.Ordinal) ||
        !await AuthorizeAsync(clients, options, deviceId, principal, context.RequestAborted))
    {
        var reason = principal is null
            ? "missing_or_invalid_access_token"
            : principal.FindFirstValue("agent_id") != deviceId
                ? "token_agent_mismatch"
                : !principal.GetAudiences().Contains(requiredResource, StringComparer.Ordinal)
                    ? "token_resource_mismatch"
                    : "control_plane_authorization_failed";
        logger.LogWarning(
            "Remote MCP authentication failed {RequestId}: {Method} {Path}; reason={Reason}; device={DeviceId}; relayInstance={RelayInstanceId}",
            context.TraceIdentifier,
            context.Request.Method,
            context.Request.Path,
            reason,
            deviceId,
            relayInstance.InstanceId);
        context.Response.Headers.WWWAuthenticate = OAuthDiscovery.BearerChallenge(publicBaseUrl, deviceId, options.OAuthScopes);
        return Results.Unauthorized();
    }

    if (!registry.TryGet(deviceId, out var agent))
    {
        var snapshot = registry.Snapshot(deviceId);
        logger.LogWarning(
            "Remote MCP device_offline {RequestId}: device={DeviceId}; relayInstance={RelayInstanceId}; registryCount={RegistryCount}; currentConnection={CurrentConnectionId}; currentSocketState={CurrentSocketState}",
            context.TraceIdentifier,
            deviceId,
            relayInstance.InstanceId,
            snapshot.RegistryCount,
            snapshot.CurrentConnectionId ?? "none",
            snapshot.SocketState?.ToString() ?? "none");
        return Results.NotFound(new { error = "device_offline" });
    }

    if (context.Request.ContentLength > options.MaxBodyBytes) return Results.StatusCode(413);

    using var ms = new MemoryStream();
    await context.Request.Body.CopyToAsync(ms, context.RequestAborted);
    if (ms.Length > options.MaxBodyBytes) return Results.StatusCode(413);
    if (!ScopeAllowsPayload(principal, ms.ToArray())) return Results.Forbid();

    var headers = context.Request.Headers
        .Where(h => !string.Equals(h.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(
            h => h.Key,
            h => h.Value.Select(v => v ?? string.Empty).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    var request = new RelayRequest(Guid.NewGuid().ToString("N"), context.Request.Method, "/mcp" + context.Request.QueryString, headers, ms.Length == 0 ? null : Convert.ToBase64String(ms.ToArray()));

    RelayResponse response;
    try
    {
        response = await agent.SendAsync(request, TimeSpan.FromSeconds(options.RequestTimeoutSeconds), context.RequestAborted);
    }
    catch (TimeoutException)
    {
        logger.LogWarning(
            "Remote MCP upstream timeout {RequestId}: device={DeviceId}; connection={ConnectionId}; relayRequestId={RelayRequestId}; relayInstance={RelayInstanceId}",
            context.TraceIdentifier,
            deviceId,
            agent.ConnectionId,
            request.Id,
            relayInstance.InstanceId);
        return Results.StatusCode(504);
    }
    catch (WebSocketException ex)
    {
        logger.LogWarning(ex,
            "Remote MCP Agent transport failed {RequestId}: device={DeviceId}; connection={ConnectionId}; relayRequestId={RelayRequestId}; socketState={SocketState}; relayInstance={RelayInstanceId}",
            context.TraceIdentifier,
            deviceId,
            agent.ConnectionId,
            request.Id,
            agent.Socket.State,
            relayInstance.InstanceId);
        return Results.Json(new { error = "agent_connection_lost" }, statusCode: StatusCodes.Status502BadGateway);
    }
    catch (InvalidOperationException ex) when (agent.Socket.State != WebSocketState.Open)
    {
        logger.LogWarning(ex,
            "Remote MCP Agent transport unavailable {RequestId}: device={DeviceId}; connection={ConnectionId}; relayRequestId={RelayRequestId}; socketState={SocketState}; relayInstance={RelayInstanceId}",
            context.TraceIdentifier,
            deviceId,
            agent.ConnectionId,
            request.Id,
            agent.Socket.State,
            relayInstance.InstanceId);
        return Results.Json(new { error = "agent_connection_lost" }, statusCode: StatusCodes.Status502BadGateway);
    }

    context.Response.StatusCode = response.StatusCode;
    foreach (var h in response.Headers)
        if (!string.Equals(h.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && !string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            context.Response.Headers[h.Key] = h.Value;
    if (response.BodyBase64 is not null && !HttpMethods.IsHead(context.Request.Method))
        await context.Response.Body.WriteAsync(Convert.FromBase64String(response.BodyBase64), context.RequestAborted);
    return Results.Empty;
});

app.Run();

static async Task<ClaimsPrincipal?> AuthenticateOAuthAsync(HttpContext context)
{
    var result = await context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
    return result.Succeeded && result.Principal?.Identity?.IsAuthenticated == true ? result.Principal : null;
}

static string? Bearer(HttpContext context)
{
    var auth = context.Request.Headers.Authorization.ToString();
    return auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..] : null;
}

static async Task<bool> AuthenticateAgentAsync(IHttpClientFactory factory, RelayOptions options, string agentId, string credential, CancellationToken ct)
{
    var client = factory.CreateClient("control-plane");
    using var request = new HttpRequestMessage(HttpMethod.Post, "internal/agents/authenticate") { Content = JsonContent.Create(new { agentId, credential }) };
    request.Headers.Add("X-MateMCP-Internal-Key", options.InternalApiKey);
    using var response = await client.SendAsync(request, ct);
    return response.IsSuccessStatusCode;
}

static async Task MarkAgentOfflineAsync(IHttpClientFactory factory, RelayOptions options, string agentId, DateTimeOffset lastSeenAt)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    try
    {
        var client = factory.CreateClient("control-plane");
        using var request = new HttpRequestMessage(HttpMethod.Post, "internal/agents/offline") { Content = JsonContent.Create(new { agentId, lastSeenAt }) };
        request.Headers.Add("X-MateMCP-Internal-Key", options.InternalApiKey);
        using var response = await client.SendAsync(request, timeout.Token);
    }
    catch (OperationCanceledException) { }
    catch (HttpRequestException) { }
}

static async Task RunAgentHeartbeatAsync(IHttpClientFactory factory, RelayOptions options, string agentId, string credential, CancellationToken ct)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
    try
    {
        while (await timer.WaitForNextTickAsync(ct))
        {
            using var heartbeatTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            heartbeatTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await AuthenticateAgentAsync(factory, options, agentId, credential, heartbeatTimeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // A transient control-plane timeout must not tear down an otherwise healthy relay connection.
            }
            catch (HttpRequestException)
            {
                // A transient control-plane/network failure will be retried on the next heartbeat.
            }
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
    }
}

static async Task<bool> AuthorizeAsync(IHttpClientFactory factory, RelayOptions options, string agentId, ClaimsPrincipal principal, CancellationToken ct)
{
    var userId = principal.FindFirstValue(OpenIddict.Abstractions.OpenIddictConstants.Claims.Subject);
    if (string.IsNullOrWhiteSpace(userId)) return false;
    var client = factory.CreateClient("control-plane");
    using var request = new HttpRequestMessage(HttpMethod.Post, "internal/agents/authorize") { Content = JsonContent.Create(new { agentId, userId, scopes = principal.GetScopes() }) };
    request.Headers.Add("X-MateMCP-Internal-Key", options.InternalApiKey);
    using var response = await client.SendAsync(request, ct);
    return response.IsSuccessStatusCode;
}

static bool ScopeAllowsPayload(ClaimsPrincipal principal, byte[] body)
{
    if (body.Length == 0) return principal.HasScope("mcp:read");
    try
    {
        using var document = JsonDocument.Parse(body);
        var calls = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray() : [document.RootElement];
        foreach (var call in calls)
        {
            if (!call.TryGetProperty("method", out var method) || method.GetString() != "tools/call")
            {
                if (!principal.HasScope("mcp:read")) return false;
                continue;
            }
            var name = call.TryGetProperty("params", out var parameters) && parameters.TryGetProperty("name", out var tool) ? tool.GetString() : null;
            var required = McpScopePolicy.RequiredScopeForTool(name);
            if (!principal.HasScope(required)) return false;
        }
        return true;
    }
    catch (JsonException) { return false; }
}
