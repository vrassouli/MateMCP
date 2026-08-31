using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MateMCP.Api.Data;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var publicUrl = configuration["MateMCP:PublicUrl"]?.TrimEnd('/') ?? "https://api.matemcp.com";
var relayUrl = configuration["MateMCP:RelayUrl"]?.TrimEnd('/') ?? "https://relay.matemcp.com";
var provider = configuration["MateMCP:DatabaseProvider"]?.ToLowerInvariant() ?? "sqlite";
var encodedConnectionString = configuration["MateMCP:ConnectionStringBase64"];
var connectionString = string.IsNullOrWhiteSpace(encodedConnectionString) ? configuration.GetConnectionString("MateMCP") ?? "Data Source=/data/matemcp-api.db" : Encoding.UTF8.GetString(Convert.FromBase64String(encodedConnectionString));
var internalKey = configuration["MateMCP:InternalApiKey"] ?? throw new InvalidOperationException("Configure MateMCP:InternalApiKey.");
var dataDirectory = configuration["MateMCP:KeyPath"] ?? "/data";
Directory.CreateDirectory(dataDirectory);

builder.Services.Configure<ForwardedHeadersOptions>(o => { o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto; o.KnownIPNetworks.Clear(); o.KnownProxies.Clear(); });
builder.Services.AddDbContext<ControlPlaneDbContext>(o =>
{
    if (provider == "sqlserver") o.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure());
    else if (provider == "sqlite") o.UseSqlite(connectionString);
    else throw new InvalidOperationException("MateMCP:DatabaseProvider must be sqlite or sqlserver.");
    o.UseOpenIddict();
});
builder.Services.AddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => { o.Cookie.Name = "matemcp.auth"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Lax; o.Cookie.SecurePolicy = CookieSecurePolicy.Always; o.LoginPath = "/login"; });
builder.Services.AddAuthorization();
builder.Services.AddOpenIddict().AddCore(o => o.UseEntityFrameworkCore().UseDbContext<ControlPlaneDbContext>()).AddServer(o =>
{
    o.SetIssuer(new Uri(publicUrl + "/")); o.SetAuthorizationEndpointUris("/connect/authorize"); o.SetTokenEndpointUris("/connect/token"); o.SetJsonWebKeySetEndpointUris("/.well-known/jwks");
    o.AllowAuthorizationCodeFlow(); o.AllowRefreshTokenFlow(); o.RequireProofKeyForCodeExchange();
    o.RegisterScopes("mcp:read", "mcp:write", "mcp:shell", OpenIddictConstants.Scopes.OfflineAccess); o.DisableResourceValidation(); o.IgnoreResourcePermissions();
    o.AddSigningKey(new RsaSecurityKey(LoadOrCreateRsaKey(Path.Combine(dataDirectory, "signing-key.pem")))); o.AddEncryptionKey(new RsaSecurityKey(LoadOrCreateRsaKey(Path.Combine(dataDirectory, "encryption-key.pem")))); o.DisableAccessTokenEncryption();
    o.UseAspNetCore().EnableAuthorizationEndpointPassthrough();
});

var app = builder.Build(); app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    if (context.Request.Method == HttpMethods.Get && (context.Request.Path == "/.well-known/oauth-authorization-server" || context.Request.Path == "/.well-known/openid-configuration"))
    {
        await context.Response.WriteAsJsonAsync(new { issuer = publicUrl + "/", authorization_endpoint = publicUrl + "/connect/authorize", token_endpoint = publicUrl + "/connect/token", registration_endpoint = publicUrl + "/connect/register", jwks_uri = publicUrl + "/.well-known/jwks", response_types_supported = new[] { "code" }, grant_types_supported = new[] { "authorization_code", "refresh_token" }, code_challenge_methods_supported = new[] { "S256" }, token_endpoint_auth_methods_supported = new[] { "none" }, scopes_supported = new[] { "mcp:read", "mcp:write", "mcp:shell", "offline_access" }, authorization_response_iss_parameter_supported = true }); return;
    }
    await next();
});
app.UseAuthentication(); app.UseAuthorization(); await EnsureDatabaseAsync(app.Services, configuration);
app.MapGet("/health", () => Results.Ok(new { service = "MateMCP.Api", status = "ok", database = provider }));
app.MapGet("/", (ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true ? Results.Redirect("/dashboard") : Results.Redirect("/login"));

app.MapGet("/register", () => Page("Create MateMCP account", "<form method=post><label>Email<input name=email type=email required></label><label>Password<input name=password type=password minlength=10 required></label><button>Create account</button></form><p><a href=/login>Sign in</a></p>"));
app.MapPost("/register", async (HttpContext context, ControlPlaneDbContext db, IPasswordHasher<UserAccount> hasher) =>
{
    var form = await context.Request.ReadFormAsync(); var email = form["email"].ToString().Trim(); var password = form["password"].ToString();
    if (password.Length < 10 || !email.Contains('@')) return Results.BadRequest("A valid email and a password of at least 10 characters are required.");
    var normalized = email.ToUpperInvariant(); if (await db.Users.AnyAsync(x => x.NormalizedEmail == normalized)) return Results.Conflict("Account already exists.");
    var account = new UserAccount { Email = email, NormalizedEmail = normalized, PasswordHash = "pending" }; account.PasswordHash = hasher.HashPassword(account, password); db.Users.Add(account); await db.SaveChangesAsync(); await SignInAsync(context, account); return Results.Redirect("/dashboard");
});
app.MapGet("/login", (string? returnUrl) => Page("Sign in", $"<form method=post><input type=hidden name=returnUrl value=\"{H(returnUrl ?? "/dashboard")}\"><label>Email<input name=email type=email required></label><label>Password<input name=password type=password required></label><button>Sign in</button></form><p><a href=/register>Create account</a></p>"));
app.MapPost("/login", async (HttpContext context, ControlPlaneDbContext db, IPasswordHasher<UserAccount> hasher) =>
{
    var form = await context.Request.ReadFormAsync(); var account = await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == form["email"].ToString().Trim().ToUpperInvariant() && !x.IsDisabled);
    if (account is null || hasher.VerifyHashedPassword(account, account.PasswordHash, form["password"].ToString()) == PasswordVerificationResult.Failed) return Results.Unauthorized();
    await SignInAsync(context, account); var returnUrl = form["returnUrl"].ToString(); return Results.Redirect(IsLocal(returnUrl) ? returnUrl : "/dashboard");
});
app.MapPost("/logout", async (HttpContext context) => { await context.SignOutAsync(); return Results.Redirect("/login"); });

app.MapGet("/dashboard", async (ClaimsPrincipal principal, ControlPlaneDbContext db) =>
{
    var userId = UserId(principal); var agents = await db.Agents.Where(x => x.OwnerId == userId).OrderBy(x => x.Name).ToListAsync();
    var now = DateTimeOffset.UtcNow;
    var approvals = (await db.Approvals.Include(x => x.AgentDevice).Where(x => x.AgentDevice!.OwnerId == userId && x.Status == "pending").ToListAsync()).Where(x => x.ExpiresAt > now).OrderBy(x => x.CreatedAt).ToList();
    var rows = string.Join("", agents.Select(x => $"<tr><td>{H(x.Name)}</td><td>{H(x.Platform)}</td><td>{(x.IsRevoked ? "revoked" : x.LastSeenAt > DateTimeOffset.UtcNow.AddMinutes(-2) ? "online" : "offline")}</td><td><code>{relayUrl}/mcp/{x.PublicId}</code></td><td>{(x.IsRevoked ? "" : $"<form method=post action=/dashboard/agents/{x.PublicId}/revoke><button class=deny>Revoke</button></form>")}</td></tr>"));
    var pending = string.Join("", approvals.Select(x => $"<article><b>{H(x.Capability)}</b> on {H(x.AgentDevice!.Name)}<pre>{H(x.Summary)}</pre><form method=post action=/dashboard/approvals/{x.Id}/allow><button>Allow once</button></form><form method=post action=/dashboard/approvals/{x.Id}/deny><button class=deny>Deny</button></form></article>"));
    return Page("Your MateMCP agents", $"<table><tr><th>Name</th><th>Platform</th><th>Status</th><th>MCP URL</th><th></th></tr>{rows}</table><h2>Pending approvals</h2>{(pending.Length == 0 ? "<p>Nothing waiting.</p>" : pending)}<form method=post action=/logout><button>Sign out</button></form>");
}).RequireAuthorization();
app.MapPost("/dashboard/agents/{agentId}/revoke", async (string agentId, ClaimsPrincipal principal, ControlPlaneDbContext db) =>
{
    var userId = UserId(principal); var agent = await db.Agents.SingleOrDefaultAsync(x => x.PublicId == agentId && x.OwnerId == userId && !x.IsRevoked); if (agent is null) return Results.NotFound(); agent.IsRevoked = true; db.AuditEvents.Add(new AuditEvent { UserId = userId, AgentDeviceId = agent.Id, EventType = "agent.revoked", Detail = agent.Name }); await db.SaveChangesAsync(); return Results.Redirect("/dashboard");
}).RequireAuthorization();

app.MapPost("/api/enrollment/start", async (EnrollmentStart request, ControlPlaneDbContext db) =>
{
    var raw = Token(32); var code = CreateUserCode(); db.Enrollments.Add(new EnrollmentSession { DeviceCodeHash = Hash(raw), UserCode = code, DeviceName = request.Name.Trim(), Platform = request.Platform.Trim(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) }); await db.SaveChangesAsync();
    return Results.Ok(new { deviceCode = raw, userCode = code, verificationUri = publicUrl + "/device", verificationUriComplete = publicUrl + "/device?code=" + code, interval = 3, expiresIn = 600 });
});
app.MapGet("/device", (string? code) => Page("Add a device", $"<form method=post action=/device/approve><label>Code<input name=code value=\"{H(code ?? "")}\" required></label><button>Add device</button></form>")).RequireAuthorization();
app.MapPost("/device/approve", async (HttpContext context, ClaimsPrincipal principal, ControlPlaneDbContext db) =>
{
    var form = await context.Request.ReadFormAsync(); var code = form["code"].ToString().Trim().ToUpperInvariant(); var enrollment = await db.Enrollments.SingleOrDefaultAsync(x => x.UserCode == code && !x.Consumed && x.ApprovedByUserId == null);
    if (enrollment is null || enrollment.ExpiresAt <= DateTimeOffset.UtcNow) return Results.BadRequest("Invalid or expired code."); enrollment.ApprovedByUserId = UserId(principal); await db.SaveChangesAsync(); return Page("Device approved", $"<p><b>{H(enrollment.DeviceName)}</b> can now finish setup. You may close this page.</p>");
}).RequireAuthorization();
app.MapPost("/api/enrollment/token", async (EnrollmentToken request, ControlPlaneDbContext db) =>
{
    var deviceCodeHash = Hash(request.DeviceCode); var enrollment = await db.Enrollments.SingleOrDefaultAsync(x => x.DeviceCodeHash == deviceCodeHash); if (enrollment is null || enrollment.ExpiresAt <= DateTimeOffset.UtcNow) return Results.BadRequest(new { error = "expired_token" }); if (enrollment.ApprovedByUserId is null) return Results.StatusCode(428); if (enrollment.Consumed) return Results.BadRequest(new { error = "invalid_grant" });
    var credential = Token(48); var agent = new AgentDevice { PublicId = "agt_" + Token(18), OwnerId = enrollment.ApprovedByUserId.Value, Name = enrollment.DeviceName, Platform = enrollment.Platform, CredentialHash = Hash(credential) }; enrollment.Consumed = true; db.Agents.Add(agent); db.AuditEvents.Add(new AuditEvent { UserId = agent.OwnerId, AgentDeviceId = agent.Id, EventType = "agent.enrolled", Detail = agent.Name }); await db.SaveChangesAsync();
    return Results.Ok(new { agentId = agent.PublicId, credential, relayUrl, mcpUrl = $"{relayUrl}/mcp/{agent.PublicId}" });
});

app.MapMethods("/connect/authorize", ["GET", "POST"], async (HttpContext context, ControlPlaneDbContext db) =>
{
    var request = context.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("OIDC request unavailable."); if (context.User.Identity?.IsAuthenticated != true) return Results.Redirect("/login?returnUrl=" + Uri.EscapeDataString(context.Request.Path + context.Request.QueryString));
    var resources = request.GetResources(); if (resources.Length != 1 || !TryAgentId(resources[0], relayUrl, out var publicId)) return Results.BadRequest(new { error = OpenIddictConstants.Errors.InvalidTarget });
    var userId = UserId(context.User); var agent = await db.Agents.SingleOrDefaultAsync(x => x.PublicId == publicId && x.OwnerId == userId && !x.IsRevoked); if (agent is null) return Results.Forbid();
    var scopes = request.GetScopes().Intersect(agent.AllowedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray(); var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, OpenIddictConstants.Claims.Name, ClaimTypes.Role);
    identity.AddClaim(OpenIddictConstants.Claims.Subject, userId.ToString()); identity.AddClaim(OpenIddictConstants.Claims.Name, context.User.Identity!.Name!); identity.AddClaim("agent_id", agent.PublicId); identity.SetScopes(scopes); identity.SetResources(resources[0], relayUrl); identity.SetDestinations(_ => [OpenIddictConstants.Destinations.AccessToken]);
    return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
});
app.MapPost("/connect/register", async (HttpContext context, IOpenIddictApplicationManager manager) =>
{
    var r = await context.Request.ReadFromJsonAsync<ClientRegistration>(); if (r?.RedirectUris is null || r.RedirectUris.Length == 0 || r.RedirectUris.Any(x => !Uri.TryCreate(x, UriKind.Absolute, out _))) return Results.BadRequest(new { error = "invalid_client_metadata" });
    var d = new OpenIddictApplicationDescriptor { ClientId = Guid.NewGuid().ToString("N"), ClientType = OpenIddictConstants.ClientTypes.Public, ConsentType = OpenIddictConstants.ConsentTypes.Implicit, DisplayName = string.IsNullOrWhiteSpace(r.ClientName) ? "MCP Client" : r.ClientName }; foreach (var uri in r.RedirectUris) d.RedirectUris.Add(new Uri(uri));
    d.Permissions.UnionWith([OpenIddictConstants.Permissions.Endpoints.Authorization, OpenIddictConstants.Permissions.Endpoints.Token, OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode, OpenIddictConstants.Permissions.GrantTypes.RefreshToken, OpenIddictConstants.Permissions.ResponseTypes.Code, OpenIddictConstants.Permissions.Prefixes.Scope + "mcp:read", OpenIddictConstants.Permissions.Prefixes.Scope + "mcp:write", OpenIddictConstants.Permissions.Prefixes.Scope + "mcp:shell", OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess]); await manager.CreateAsync(d);
    return Results.Ok(new { client_id = d.ClientId, client_name = d.DisplayName, redirect_uris = r.RedirectUris, token_endpoint_auth_method = "none", grant_types = new[] { "authorization_code", "refresh_token" }, response_types = new[] { "code" } });
});

app.MapPost("/internal/agents/authenticate", async (HttpContext c, AgentAuthentication r, ControlPlaneDbContext db) =>
{
    if (!Internal(c, internalKey)) return Results.Unauthorized(); var credentialHash = Hash(r.Credential); var agent = await db.Agents.SingleOrDefaultAsync(x => x.PublicId == r.AgentId && x.CredentialHash == credentialHash && !x.IsRevoked); if (agent is null) return Results.Unauthorized(); agent.LastSeenAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); return Results.Ok(new { agentId = agent.PublicId, ownerId = agent.OwnerId, scopes = agent.AllowedScopes.Split(' ') });
});
app.MapPost("/internal/agents/authorize", async (HttpContext c, AgentAuthorization r, ControlPlaneDbContext db) =>
{
    if (!Internal(c, internalKey)) return Results.Unauthorized(); var agent = await db.Agents.SingleOrDefaultAsync(x => x.PublicId == r.AgentId && !x.IsRevoked); if (agent is null || agent.OwnerId.ToString() != r.UserId) return Results.Forbid(); var allowed = agent.AllowedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries); return r.Scopes.All(allowed.Contains) ? Results.Ok() : Results.Forbid();
});
app.MapPost("/api/agents/{agentId}/approvals", async (string agentId, HttpContext c, NewApproval r, ControlPlaneDbContext db) =>
{
    var agent = await AuthenticateAgent(c, agentId, db); if (agent is null) return Results.Unauthorized(); var approval = new ApprovalRequest { AgentDeviceId = agent.Id, Capability = r.Capability, Target = r.Target, Summary = r.Summary, OperationHash = Hash(r.Capability + "\n" + r.Target + "\n" + r.Summary), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(r.ExpiresIn, 15, 600)) }; db.Approvals.Add(approval); await db.SaveChangesAsync(); return Results.Ok(new { id = approval.Id, status = approval.Status, expiresAt = approval.ExpiresAt });
});
app.MapGet("/api/agents/{agentId}/approvals/{id:guid}", async (string agentId, Guid id, HttpContext c, ControlPlaneDbContext db) =>
{
    var agent = await AuthenticateAgent(c, agentId, db); if (agent is null) return Results.Unauthorized(); var a = await db.Approvals.SingleOrDefaultAsync(x => x.Id == id && x.AgentDeviceId == agent.Id); if (a is null) return Results.NotFound(); if (a.Status == "pending" && a.ExpiresAt <= DateTimeOffset.UtcNow) { a.Status = "expired"; await db.SaveChangesAsync(); } return Results.Ok(new { a.Status });
});
app.MapPost("/dashboard/approvals/{id:guid}/{decision}", async (Guid id, string decision, ClaimsPrincipal principal, ControlPlaneDbContext db) =>
{
    if (decision is not ("allow" or "deny")) return Results.NotFound(); var userId = UserId(principal); var a = await db.Approvals.Include(x => x.AgentDevice).SingleOrDefaultAsync(x => x.Id == id && x.AgentDevice!.OwnerId == userId && x.Status == "pending"); if (a is null || a.ExpiresAt <= DateTimeOffset.UtcNow) return Results.NotFound(); a.Status = decision == "allow" ? "allowed" : "denied"; a.DecidedAt = DateTimeOffset.UtcNow; db.AuditEvents.Add(new AuditEvent { UserId = userId, AgentDeviceId = a.AgentDeviceId, EventType = "approval." + a.Status, Detail = a.OperationHash }); await db.SaveChangesAsync(); return Results.Redirect("/dashboard");
}).RequireAuthorization();

app.Run();

static async Task EnsureDatabaseAsync(IServiceProvider services, IConfiguration c) { await using var s = services.CreateAsyncScope(); var db = s.ServiceProvider.GetRequiredService<ControlPlaneDbContext>(); await db.Database.EnsureCreatedAsync(); var email = c["MateMCP:BootstrapAdminEmail"]; var password = c["MateMCP:BootstrapAdminPassword"]; if (!string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(password) && !await db.Users.AnyAsync()) { var h = s.ServiceProvider.GetRequiredService<IPasswordHasher<UserAccount>>(); var u = new UserAccount { Email = email, NormalizedEmail = email.ToUpperInvariant(), PasswordHash = "pending", IsAdmin = true }; u.PasswordHash = h.HashPassword(u, password); db.Users.Add(u); await db.SaveChangesAsync(); } }
static async Task SignInAsync(HttpContext c, UserAccount u) { var i = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme); i.AddClaim(new Claim(ClaimTypes.NameIdentifier, u.Id.ToString())); i.AddClaim(new Claim(ClaimTypes.Name, u.Email)); await c.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(i)); }
static Guid UserId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);
static string Token(int bytes) => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(bytes));
static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
static string CreateUserCode() { const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; var b = RandomNumberGenerator.GetBytes(8); return string.Concat(b.Select((x, i) => chars[x % chars.Length] + (i == 3 ? "-" : ""))); }
static bool Internal(HttpContext c, string expected) => SecretEquals(c.Request.Headers["X-MateMCP-Internal-Key"].ToString(), expected);
static async Task<AgentDevice?> AuthenticateAgent(HttpContext c, string id, ControlPlaneDbContext db) { var auth = c.Request.Headers.Authorization.ToString(); if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null; var credentialHash = Hash(auth[7..]); return await db.Agents.SingleOrDefaultAsync(x => x.PublicId == id && x.CredentialHash == credentialHash && !x.IsRevoked); }
static bool SecretEquals(string a, string b) { var x = Encoding.UTF8.GetBytes(a); var y = Encoding.UTF8.GetBytes(b); return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y); }
static bool TryAgentId(string resource, string relay, out string id) { var prefix = relay + "/mcp/"; id = resource.StartsWith(prefix, StringComparison.Ordinal) ? resource[prefix.Length..] : ""; return id.StartsWith("agt_", StringComparison.Ordinal) && !id.Contains('/'); }
static bool IsLocal(string s) => !string.IsNullOrEmpty(s) && s[0] == '/' && (s.Length == 1 || s[1] != '/' && s[1] != '\\');
static string H(string s) => System.Net.WebUtility.HtmlEncode(s);
static IResult Page(string title, string body) => Results.Content($"<!doctype html><html><head><meta charset=utf-8><meta name=viewport content=\"width=device-width\"><title>{H(title)}</title><style>body{{font-family:system-ui;max-width:900px;margin:50px auto;padding:0 20px;color:#17202a}}label{{display:block;margin:14px 0}}input{{display:block;width:100%;max-width:420px;padding:10px}}button{{padding:10px 16px;margin:4px}}.deny{{background:#a22;color:white}}table{{border-collapse:collapse;width:100%}}td,th{{padding:9px;border-bottom:1px solid #ddd;text-align:left}}code,pre{{overflow-wrap:anywhere}}article{{border:1px solid #ddd;padding:14px;margin:12px 0}}</style></head><body><h1>{H(title)}</h1>{body}</body></html>", "text/html");
static RSA LoadOrCreateRsaKey(string path) { var rsa = RSA.Create(3072); if (File.Exists(path)) { rsa.ImportFromPem(File.ReadAllText(path)); return rsa; } File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem()); return rsa; }

sealed record EnrollmentStart(string Name, string Platform);
sealed record EnrollmentToken(string DeviceCode);
sealed record AgentAuthentication(string AgentId, string Credential);
sealed record AgentAuthorization(string AgentId, string UserId, string[] Scopes);
sealed record NewApproval(string Capability, string Target, string Summary, int ExpiresIn = 120);
sealed record ClientRegistration([property: System.Text.Json.Serialization.JsonPropertyName("client_name")] string? ClientName, [property: System.Text.Json.Serialization.JsonPropertyName("redirect_uris")] string[]? RedirectUris);
