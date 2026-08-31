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
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddHttpClient("control-plane", client => client.BaseAddress = new Uri((builder.Configuration["Relay:ControlPlaneUrl"] ?? "https://api.matemcp.com").TrimEnd('/') + "/"));

var options = builder.Configuration.GetSection(RelayOptions.SectionName).Get<RelayOptions>() ?? new RelayOptions();
if (options.InternalApiKey == "change-me")
    throw new InvalidOperationException("Configure Relay:InternalApiKey.");

var publicBaseUrl = options.PublicBaseUrl.TrimEnd('/');
var authorizationServerUrl = options.AuthorizationServerUrl.TrimEnd('/');
var scopeValue = string.Join(' ', options.OAuthScopes);

builder.Services.AddOpenIddict().AddValidation(validation =>
{
    validation.SetIssuer(new Uri(authorizationServerUrl + "/"));
    validation.UseSystemNetHttp();
    validation.UseAspNetCore();
});

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20),
    KeepAliveTimeout = TimeSpan.FromSeconds(20)
});

app.MapGet("/health", () => Results.Ok(new { service = "MateMCP.Relay", status = "ok" }));

app.MapGet("/.well-known/oauth-protected-resource", () => Results.Ok(new
{
    resource = publicBaseUrl,
    authorization_servers = new[] { authorizationServerUrl },
    scopes_supported = options.OAuthScopes,
    bearer_methods_supported = new[] { "header" },
    resource_documentation = "https://github.com/vrassouli/MateMCP"
}));

app.MapGet("/.well-known/oauth-protected-resource/mcp/{deviceId}", (string deviceId) => Results.Ok(new
{
    resource = $"{publicBaseUrl}/mcp/{deviceId}",
    authorization_servers = new[] { authorizationServerUrl },
    scopes_supported = options.OAuthScopes,
    bearer_methods_supported = new[] { "header" }
}));

app.Map("/relay/agent/{deviceId}", async (HttpContext context, string deviceId, AgentRegistry registry, IHttpClientFactory clients) =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    var credential = Bearer(context);
    if (credential is null || !await AuthenticateAgentAsync(clients, options, deviceId, credential, context.RequestAborted)) { context.Response.StatusCode = 401; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    if (!registry.TryRegister(deviceId, socket, out var connection))
    {
        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "device already connected", context.RequestAborted);
        return;
    }

    using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var heartbeatTask = RunAgentHeartbeatAsync(clients, options, deviceId, credential, heartbeatCts.Token);
    var buffer = new byte[64 * 1024];
    try
    {
        while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
                if (ms.Length > options.MaxBodyBytes * 2L) throw new InvalidOperationException("Relay response too large.");
            } while (!result.EndOfMessage);

            var response = JsonSerializer.Deserialize(ms.ToArray(), RelayJsonContext.Default.RelayResponse);
            if (response is not null) connection.Complete(response);
        }
    }
    finally
    {
        heartbeatCts.Cancel();
        await heartbeatTask;
        registry.Remove(deviceId, connection);
    }
});

app.MapMethods("/mcp/{deviceId}", ["GET", "POST", "DELETE", "PUT", "PATCH"], async (HttpContext context, string deviceId, AgentRegistry registry, IHttpClientFactory clients) =>
{
    var principal = await AuthenticateOAuthAsync(context);
    var requiredResource = $"{publicBaseUrl}/mcp/{deviceId}";
    if (principal is null || principal.FindFirstValue("agent_id") != deviceId || !principal.GetAudiences().Contains(requiredResource, StringComparer.Ordinal) ||
        !await AuthorizeAsync(clients, options, deviceId, principal, context.RequestAborted))
    {
        context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{publicBaseUrl}/.well-known/oauth-protected-resource/mcp/{deviceId}\", scope=\"{scopeValue}\"";
        return Results.Unauthorized();
    }
    if (!registry.TryGet(deviceId, out var agent)) return Results.NotFound(new { error = "device_offline" });
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
    try { response = await agent.SendAsync(request, TimeSpan.FromSeconds(options.RequestTimeoutSeconds), context.RequestAborted); }
    catch (TimeoutException) { return Results.StatusCode(504); }

    context.Response.StatusCode = response.StatusCode;
    foreach (var h in response.Headers)
        if (!string.Equals(h.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && !string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            context.Response.Headers[h.Key] = h.Value;
    if (response.BodyBase64 is not null)
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
            var required = name switch { "filesystem_write" => "mcp:write", "shell_exec" => "mcp:shell", _ => "mcp:read" };
            if (!principal.HasScope(required)) return false;
        }
        return true;
    }
    catch (JsonException) { return false; }
}