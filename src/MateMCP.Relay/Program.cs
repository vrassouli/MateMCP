using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using MateMCP.Relay;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Validation.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<RelayOptions>(builder.Configuration.GetSection(RelayOptions.SectionName));
builder.Services.AddSingleton<AgentRegistry>();

var options = builder.Configuration.GetSection(RelayOptions.SectionName).Get<RelayOptions>() ?? new RelayOptions();
if (options.AgentToken == "change-me" || options.ClientToken == "change-me")
    throw new InvalidOperationException("Configure Relay:AgentToken and Relay:ClientToken.");

var publicBaseUrl = options.PublicBaseUrl.TrimEnd('/');
var authorizationServerUrl = options.AuthorizationServerUrl.TrimEnd('/');
var resourceMetadataUrl = $"{publicBaseUrl}/.well-known/oauth-protected-resource";
var scopeValue = string.Join(' ', options.OAuthScopes);

builder.Services.AddOpenIddict().AddValidation(validation =>
{
    validation.SetIssuer(new Uri(authorizationServerUrl + "/"));
    validation.AddAudiences(publicBaseUrl);
    validation.UseSystemNetHttp();
    validation.UseAspNetCore();
});

var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.MapGet("/health", () => Results.Ok(new { service = "MateMCP.Relay", status = "ok" }));

app.MapGet("/.well-known/oauth-protected-resource", () => Results.Ok(new
{
    resource = publicBaseUrl,
    authorization_servers = new[] { authorizationServerUrl },
    scopes_supported = options.OAuthScopes,
    bearer_methods_supported = new[] { "header" },
    resource_documentation = "https://github.com/vrassouli/MateMCP"
}));

app.Map("/relay/agent/{deviceId}", async (HttpContext context, string deviceId, AgentRegistry registry) =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    if (!BearerEquals(context, options.AgentToken)) { context.Response.StatusCode = 401; return; }
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    if (!registry.TryRegister(deviceId, socket, out var connection))
    {
        await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "device already connected", context.RequestAborted);
        return;
    }

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
    finally { registry.Remove(deviceId, connection); }
});

app.MapMethods("/mcp/{deviceId}", ["GET", "POST", "DELETE", "PUT", "PATCH"], async (HttpContext context, string deviceId, AgentRegistry registry) =>
{
    if (!BearerEquals(context, options.ClientToken) && !await HasValidOAuthTokenAsync(context))
    {
        context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{resourceMetadataUrl}\", scope=\"{scopeValue}\"";
        return Results.Unauthorized();
    }
    if (!registry.TryGet(deviceId, out var agent)) return Results.NotFound(new { error = "device_offline" });
    if (context.Request.ContentLength > options.MaxBodyBytes) return Results.StatusCode(413);

    using var ms = new MemoryStream();
    await context.Request.Body.CopyToAsync(ms, context.RequestAborted);
    if (ms.Length > options.MaxBodyBytes) return Results.StatusCode(413);

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

static async Task<bool> HasValidOAuthTokenAsync(HttpContext context)
{
    var result = await context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
    return result.Succeeded && result.Principal?.Identity?.IsAuthenticated == true;
}

static bool BearerEquals(HttpContext context, string expected)
{
    var auth = context.Request.Headers.Authorization.ToString();
    if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
    var supplied = System.Text.Encoding.UTF8.GetBytes(auth[7..]);
    var required = System.Text.Encoding.UTF8.GetBytes(expected);
    return supplied.Length == required.Length && CryptographicOperations.FixedTimeEquals(supplied, required);
}
