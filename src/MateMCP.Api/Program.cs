using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var publicUrl = builder.Configuration["MateMCP:PublicUrl"]?.TrimEnd('/') ?? "https://api.matemcp.com";
var relayResource = builder.Configuration["MateMCP:RelayResource"]?.TrimEnd('/') ?? "https://relay.matemcp.com";
var adminEmail = builder.Configuration["MateMCP:AdminEmail"] ?? "admin@matemcp.local";
var adminPassword = builder.Configuration["MateMCP:AdminPassword"];
var dataPath = builder.Configuration["MateMCP:DataPath"] ?? "/data/matemcp-api.db";
var dataDirectory = Path.GetDirectoryName(dataPath) ?? "/data";

if (string.IsNullOrWhiteSpace(adminPassword))
    throw new InvalidOperationException("Configure MateMCP:AdminPassword.");

Directory.CreateDirectory(dataDirectory);
var signingKey = LoadOrCreateRsaKey(Path.Combine(dataDirectory, "signing-key.pem"));
var encryptionKey = LoadOrCreateRsaKey(Path.Combine(dataDirectory, "encryption-key.pem"));

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddDbContext<OAuthDbContext>(options =>
{
    options.UseSqlite($"Data Source={dataPath}");
    options.UseOpenIddict();
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "matemcp.api.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.LoginPath = "/login";
    });

builder.Services.AddAuthorization();

builder.Services.AddOpenIddict()
    .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<OAuthDbContext>())
    .AddServer(options =>
    {
        options.SetIssuer(new Uri(publicUrl + "/"));
        options.SetAuthorizationEndpointUris("/connect/authorize");
        options.SetTokenEndpointUris("/connect/token");
        options.SetJsonWebKeySetEndpointUris("/.well-known/jwks");

        options.AllowAuthorizationCodeFlow();
        options.AllowRefreshTokenFlow();
        options.RequireProofKeyForCodeExchange();
        options.RegisterScopes("mcp:read", "mcp:write", "mcp:shell", OpenIddictConstants.Scopes.OfflineAccess);
        options.RegisterResources(relayResource);

        options.AddSigningKey(new RsaSecurityKey(signingKey));
        options.AddEncryptionKey(new RsaSecurityKey(encryptionKey));
        options.DisableAccessTokenEncryption();

        options.UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough();
    });

var app = builder.Build();

app.UseForwardedHeaders();

// OpenIddict publishes its own discovery document, but MCP clients such as ChatGPT
// need the Dynamic Client Registration endpoint and public-client ("none") token
// authentication to be advertised explicitly. Serve the standards-based metadata
// before OpenIddict's middleware can handle the request.
app.Use(async (context, next) =>
{
    if (context.Request.Method == HttpMethods.Get &&
        (context.Request.Path.Equals("/.well-known/oauth-authorization-server") ||
         context.Request.Path.Equals("/.well-known/openid-configuration")))
    {
        await context.Response.WriteAsJsonAsync(new
        {
            issuer = publicUrl + "/",
            authorization_endpoint = publicUrl + "/connect/authorize",
            token_endpoint = publicUrl + "/connect/token",
            registration_endpoint = publicUrl + "/connect/register",
            jwks_uri = publicUrl + "/.well-known/jwks",
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "refresh_token" },
            code_challenge_methods_supported = new[] { "S256" },
            token_endpoint_auth_methods_supported = new[] { "none" },
            scopes_supported = new[] { "mcp:read", "mcp:write", "mcp:shell", "offline_access" }
        });
        return;
    }

    await next();
});

app.UseAuthentication();
app.UseAuthorization();

await EnsureDatabaseAsync(app.Services);

app.MapGet("/", () => Results.Ok(new
{
    service = "MateMCP.Api",
    authorization_server = publicUrl,
    relay_resource = relayResource
}));
app.MapGet("/health", () => Results.Ok(new { service = "MateMCP.Api", status = "ok" }));

app.MapGet("/login", (string? returnUrl) => Results.Content($$"""
<!doctype html>
<html><head><meta charset="utf-8"><title>MateMCP Login</title></head>
<body style="font-family:system-ui;max-width:420px;margin:60px auto">
<h1>MateMCP</h1>
<form method="post" action="/login">
<input type="hidden" name="returnUrl" value="{{Html(returnUrl ?? "/")}}">
<label>Email<br><input name="email" type="email" required style="width:100%;padding:8px"></label><br><br>
<label>Password<br><input name="password" type="password" required style="width:100%;padding:8px"></label><br><br>
<button type="submit">Sign in</button>
</form></body></html>
""", "text/html"));

app.MapPost("/login", async (HttpContext context) =>
{
    var form = await context.Request.ReadFormAsync();
    var email = form["email"].ToString();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();

    if (!FixedTimeEquals(email, adminEmail) || !FixedTimeEquals(password, adminPassword))
        return Results.Unauthorized();

    var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
    identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, adminEmail));
    identity.AddClaim(new Claim(ClaimTypes.Name, adminEmail));
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

    return Results.Redirect(IsLocalReturnUrl(returnUrl) ? returnUrl : "/");
});

app.MapMethods("/connect/authorize", new[] { "GET", "POST" }, async (HttpContext context) =>
{
    var request = context.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("OpenID Connect request unavailable.");

    if (context.User.Identity?.IsAuthenticated != true)
    {
        var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
        return Results.Redirect("/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
    }

    var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
    identity.AddClaim(OpenIddictConstants.Claims.Subject, context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? adminEmail);
    identity.AddClaim(OpenIddictConstants.Claims.Name, context.User.Identity?.Name ?? adminEmail);

    var requestedScopes = request.GetScopes();
    var allowedScopes = new[] { "mcp:read", "mcp:write", "mcp:shell", OpenIddictConstants.Scopes.OfflineAccess };
    identity.SetScopes(requestedScopes.Intersect(allowedScopes, StringComparer.Ordinal));
    identity.SetResources(relayResource);
    identity.SetDestinations(_ => new[] { OpenIddictConstants.Destinations.AccessToken });

    return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
});

app.MapPost("/connect/register", async (HttpContext context, IOpenIddictApplicationManager manager) =>
{
    var registration = await context.Request.ReadFromJsonAsync<ClientRegistration>();
    if (registration is null || registration.RedirectUris is null || registration.RedirectUris.Length == 0)
        return Results.BadRequest(new { error = "invalid_client_metadata" });

    if (registration.RedirectUris.Any(uri => !Uri.TryCreate(uri, UriKind.Absolute, out _)))
        return Results.BadRequest(new { error = "invalid_redirect_uri" });

    var descriptor = new OpenIddictApplicationDescriptor
    {
        ClientId = Guid.NewGuid().ToString("N"),
        ClientType = OpenIddictConstants.ClientTypes.Public,
        ConsentType = OpenIddictConstants.ConsentTypes.Implicit,
        DisplayName = string.IsNullOrWhiteSpace(registration.ClientName) ? "MCP Client" : registration.ClientName
    };

    foreach (var redirectUri in registration.RedirectUris)
        descriptor.RedirectUris.Add(new Uri(redirectUri));

    descriptor.Permissions.UnionWith(new[]
    {
        OpenIddictConstants.Permissions.Endpoints.Authorization,
        OpenIddictConstants.Permissions.Endpoints.Token,
        OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
        OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
        OpenIddictConstants.Permissions.ResponseTypes.Code,
        OpenIddictConstants.Permissions.Prefixes.Scope + "mcp:read",
        OpenIddictConstants.Permissions.Prefixes.Scope + "mcp:write",
        OpenIddictConstants.Permissions.Prefixes.Scope + "mcp:shell",
        OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess
    });

    await manager.CreateAsync(descriptor);

    return Results.Ok(new
    {
        client_id = descriptor.ClientId,
        client_name = descriptor.DisplayName,
        redirect_uris = registration.RedirectUris,
        token_endpoint_auth_method = "none",
        grant_types = new[] { "authorization_code", "refresh_token" },
        response_types = new[] { "code" }
    });
});

app.Run();

static async Task EnsureDatabaseAsync(IServiceProvider services)
{
    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<OAuthDbContext>();
    await db.Database.EnsureCreatedAsync();
}

static RSA LoadOrCreateRsaKey(string path)
{
    var rsa = RSA.Create(3072);
    if (File.Exists(path))
    {
        rsa.ImportFromPem(File.ReadAllText(path));
        return rsa;
    }

    File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem());
    return rsa;
}

static bool FixedTimeEquals(string supplied, string expected)
{
    var a = System.Text.Encoding.UTF8.GetBytes(supplied);
    var b = System.Text.Encoding.UTF8.GetBytes(expected);
    return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
}

static bool IsLocalReturnUrl(string value) => !string.IsNullOrEmpty(value) && value[0] == '/' && (value.Length == 1 || value[1] != '/' && value[1] != '\\');
static string Html(string value) => System.Net.WebUtility.HtmlEncode(value);

sealed record ClientRegistration(
    [property: System.Text.Json.Serialization.JsonPropertyName("client_name")] string? ClientName,
    [property: System.Text.Json.Serialization.JsonPropertyName("redirect_uris")] string[]? RedirectUris,
    [property: System.Text.Json.Serialization.JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod);

sealed class OAuthDbContext(DbContextOptions<OAuthDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.UseOpenIddict();
    }
}
