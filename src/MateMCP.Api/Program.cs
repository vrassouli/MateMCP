using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MateMCP.Api.Data;
using MateMCP.Api.Portal;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
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
builder.Services.AddAntiforgery(o =>
{
    o.Cookie.Name = "matemcp.csrf";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "matemcp.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.LoginPath = "/login";
    o.ReturnUrlParameter = "returnUrl";
    o.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
    o.Events.OnValidatePrincipal = async context =>
    {
        var idValue = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idValue, out var userId))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<ControlPlaneDbContext>();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId);
        if (user is null || user.IsDisabled)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        if (!string.Equals(context.Principal?.Identity?.Name, user.Email, StringComparison.Ordinal) ||
            context.Principal!.IsInRole("admin") != user.IsAdmin)
        {
            context.ReplacePrincipal(CreateCookiePrincipal(user));
            context.ShouldRenew = true;
        }
    };
});
builder.Services.AddAuthorization(o => o.AddPolicy("admin", policy => policy.RequireRole("admin")));
builder.Services.AddOpenIddict().AddCore(o => o.UseEntityFrameworkCore().UseDbContext<ControlPlaneDbContext>()).AddServer(o =>
{
    o.SetIssuer(new Uri(publicUrl + "/")); o.SetAuthorizationEndpointUris("/connect/authorize"); o.SetTokenEndpointUris("/connect/token"); o.SetJsonWebKeySetEndpointUris("/.well-known/jwks");
    o.AllowAuthorizationCodeFlow(); o.AllowRefreshTokenFlow(); o.RequireProofKeyForCodeExchange();
    o.RegisterScopes("mcp:read", "mcp:write", "mcp:shell", OpenIddictConstants.Scopes.OfflineAccess); o.DisableResourceValidation(); o.IgnoreResourcePermissions();
    o.AddSigningKey(new RsaSecurityKey(LoadOrCreateRsaKey(Path.Combine(dataDirectory, "signing-key.pem")))); o.AddEncryptionKey(new RsaSecurityKey(LoadOrCreateRsaKey(Path.Combine(dataDirectory, "encryption-key.pem")))); o.DisableAccessTokenEncryption();
    o.UseAspNetCore().EnableAuthorizationEndpointPassthrough();
});

var app = builder.Build();
app.UseForwardedHeaders();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.File.Name.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ||
            context.File.Name.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            context.File.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            context.Context.Response.Headers.CacheControl = "public,max-age=3600";
        }
    }
});
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Request.IsHttps)
            context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";

        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; img-src 'self'; style-src 'self'; script-src 'self'; connect-src 'self'; " +
            "base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
        return Task.CompletedTask;
    });
    await next();
});
app.Use(async (context, next) =>
{
    var discoveryPath = context.Request.Path.Value?.TrimEnd('/');
    if (context.Request.Method == HttpMethods.Get && (discoveryPath == "/.well-known/oauth-authorization-server" || discoveryPath == "/.well-known/openid-configuration"))
    {
        await context.Response.WriteAsJsonAsync(new { issuer = publicUrl + "/", authorization_endpoint = publicUrl + "/connect/authorize", token_endpoint = publicUrl + "/connect/token", registration_endpoint = publicUrl + "/connect/register", jwks_uri = publicUrl + "/.well-known/jwks", response_types_supported = new[] { "code" }, grant_types_supported = new[] { "authorization_code", "refresh_token" }, code_challenge_methods_supported = new[] { "S256" }, token_endpoint_auth_methods_supported = new[] { "none" }, scopes_supported = new[] { "mcp:read", "mcp:write", "mcp:shell", "offline_access" }, authorization_response_iss_parameter_supported = true }); return;
    }
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) &&
        context.Request.HasFormContentType &&
        RequiresAntiforgery(context.Request.Path))
    {
        try
        {
            await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Invalid request verification token.");
            return;
        }
    }

    await next();
});
await EnsureDatabaseAsync(app.Services, configuration);
app.MapGet("/health", () => Results.Ok(new { service = "MateMCP.Api", status = "ok", database = provider }));
app.MapGet("/", (ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true ? Results.Redirect("/dashboard") : Results.Redirect("/login"));
DeviceManagementEndpoints.Map(app, relayUrl);
PortalDeviceEndpoints.Map(app, relayUrl);
PortalApprovalEndpoints.Map(app);
PortalAdminEndpoints.Map(app);

app.MapGet("/register", (HttpContext context) =>
{
    if (context.User.Identity?.IsAuthenticated == true) return Results.Redirect("/dashboard");
    var returnUrl = SafeReturnUrl(context.Request.Query["returnUrl"].ToString());
    return PortalUi.AuthPage(context, registration: true, returnUrl);
});
app.MapPost("/register", async (HttpContext context, ControlPlaneDbContext db, IPasswordHasher<UserAccount> hasher) =>
{
    var form = await context.Request.ReadFormAsync();
    var email = form["email"].ToString().Trim();
    var password = form["password"].ToString();
    var returnUrl = SafeReturnUrl(form["returnUrl"].ToString());

    if (password.Length < 10 || !email.Contains('@'))
        return PortalUi.AuthPage(context, registration: true, returnUrl, "Enter a valid email address and a password of at least 10 characters.", email, StatusCodes.Status400BadRequest);

    var normalized = email.ToUpperInvariant();
    if (await db.Users.AnyAsync(x => x.NormalizedEmail == normalized))
        return PortalUi.AuthPage(context, registration: true, returnUrl, "An account with this email already exists.", email, StatusCodes.Status409Conflict);

    var account = new UserAccount { Email = email, NormalizedEmail = normalized, PasswordHash = "pending" };
    account.PasswordHash = hasher.HashPassword(account, password);
    db.Users.Add(account);
    await db.SaveChangesAsync();
    await SignInAsync(context, account);
    return Results.Redirect(returnUrl);
});
app.MapGet("/login", (HttpContext context) =>
{
    if (context.User.Identity?.IsAuthenticated == true) return Results.Redirect("/dashboard");
    var returnUrl = SafeReturnUrl(context.Request.Query["returnUrl"].ToString());
    return PortalUi.AuthPage(context, registration: false, returnUrl);
});
app.MapPost("/login", async (HttpContext context, ControlPlaneDbContext db, IPasswordHasher<UserAccount> hasher) =>
{
    var form = await context.Request.ReadFormAsync();
    var email = form["email"].ToString().Trim();
    var returnUrl = SafeReturnUrl(form["returnUrl"].ToString());
    var account = await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == email.ToUpperInvariant() && !x.IsDisabled);

    if (account is null || hasher.VerifyHashedPassword(account, account.PasswordHash, form["password"].ToString()) == PasswordVerificationResult.Failed)
        return PortalUi.AuthPage(context, registration: false, returnUrl, "The email or password is incorrect.", email, StatusCodes.Status401Unauthorized);

    await SignInAsync(context, account);
    return Results.Redirect(returnUrl);
});
app.MapPost("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync();
    return Results.Redirect("/login");
});

app.MapGet("/dashboard", async (HttpContext context, ClaimsPrincipal principal, ControlPlaneDbContext db) =>
{
    var userId = UserId(principal);
    var agents = await db.Agents
        .Where(x => x.OwnerId == userId && !x.IsRevoked)
        .OrderBy(x => x.Name)
        .ToListAsync();
    var now = DateTimeOffset.UtcNow;
    var onlineAfter = now.AddMinutes(-2);
    var approvals = (await db.Approvals
        .Include(x => x.AgentDevice)
        .Where(x => x.AgentDevice!.OwnerId == userId && x.Status == "pending")
        .ToListAsync())
        .Where(x => x.ExpiresAt > now)
        .OrderBy(x => x.CreatedAt)
        .ToList();

    var onlineAgents = agents.Count(x => x.LastSeenAt > onlineAfter);
    var displayAgents = agents.Take(5).ToList();
    var rows = string.Join("", displayAgents.Select(x =>
    {
        var status = x.LastSeenAt > onlineAfter ? "online" : "offline";
        return $"""
            <tr>
              <td><div class="device-name"><span class="device-icon" aria-hidden="true">{PortalUi.H(x.Platform.Length > 0 ? x.Platform[..1].ToUpperInvariant() : "D")}</span><div><strong>{PortalUi.H(x.Name)}</strong><span>{PortalUi.H(x.PublicId)}</span></div></div></td>
              <td>{PortalUi.H(x.Platform)}</td>
              <td>{PortalUi.StatusBadge(status)}</td>
              <td><code class="endpoint-code" title="{PortalUi.H(relayUrl)}/mcp/{PortalUi.H(x.PublicId)}">{PortalUi.H(relayUrl)}/mcp/{PortalUi.H(x.PublicId)}</code></td>
              <td><a class="button button-secondary button-compact" href="/devices/{PortalUi.H(x.PublicId)}">Manage</a></td>
            </tr>
            """;
    }));

    var devicesContent = rows.Length == 0
        ? PortalUi.EmptyState("No active devices", "Enroll MateMCP Desktop on a computer to see it here.", """<a class="button button-secondary" href="/device">Add a device</a>""")
        : $"""
          <div class="table-wrap">
            <table class="portal-table">
              <thead><tr><th>Device</th><th>Platform</th><th>Status</th><th>MCP endpoint</th><th></th></tr></thead>
              <tbody>{rows}</tbody>
            </table>
          </div>
          {(agents.Count > displayAgents.Count ? $"""<div class="section-footer"><a class="button button-ghost button-compact" href="/devices">View all {agents.Count} active devices</a></div>""" : "")}
          """;

    var dashboardApprovals = approvals.Take(3).ToList();
    var approvalsContent = dashboardApprovals.Count == 0
        ? PortalUi.EmptyState("Nothing waiting", "Approval requests that need your decision will appear here.")
        : $"""
          <div class="approval-list">
            {string.Join("", dashboardApprovals.Select(x => $"""
              <article class="approval-item">
                <div>
                  <div class="approval-meta"><strong>{PortalUi.H(x.Capability)}</strong>{PortalUi.StatusBadge("pending")}<span class="approval-device">on {PortalUi.H(x.AgentDevice!.Name)}</span></div>
                  <p class="approval-summary">{PortalUi.H(x.Summary)}</p>
                </div>
                <div class="approval-actions">
                  <form method="post" action="/approvals/{x.Id}/allow"><button class="button button-secondary button-compact" type="submit">Allow once</button></form>
                  <form method="post" action="/approvals/{x.Id}/deny"><button class="button button-danger button-compact" type="submit">Deny</button></form>
                </div>
              </article>
            """))}
          </div>
          """;

    var actions = """<a class="button button-ghost" href="/devices">View devices</a><a class="button button-secondary" href="/device">Add device</a>""";
    var body = $"""
        {PortalUi.PageHeading("Control plane", "Overview", "Your devices and time-sensitive approvals in one place.", actions)}
        <div class="summary-grid">
          <article class="summary-card"><div class="summary-card-head"><span class="summary-card-label">Active devices</span><span class="summary-card-icon" aria-hidden="true">D</span></div><div class="summary-value">{agents.Count}</div><div class="summary-detail">{onlineAgents} online right now</div></article>
          <article class="summary-card"><div class="summary-card-head"><span class="summary-card-label">Online</span><span class="summary-card-icon" aria-hidden="true">●</span></div><div class="summary-value">{onlineAgents}</div><div class="summary-detail">Seen in the last 2 minutes</div></article>
          <article class="summary-card"><div class="summary-card-head"><span class="summary-card-label">Needs approval</span><span class="summary-card-icon" aria-hidden="true">!</span></div><div class="summary-value">{approvals.Count}</div><div class="summary-detail">Pending decisions</div></article>
        </div>
        <div class="portal-grid">
          <section class="portal-section">
            <div class="portal-section-header"><div><h2>Active devices</h2><p>Revoked devices are kept out of this working list.</p></div><span class="section-count">{agents.Count}</span></div>
            {devicesContent}
          </section>
          <section class="portal-section" id="approvals">
            <div class="portal-section-header"><div><h2>Pending approvals</h2><p>Sensitive actions waiting for your decision.</p></div><span class="section-count">{approvals.Count}</span></div>
            {approvalsContent}
            <div class="section-footer"><a class="button button-ghost button-compact" href="/approvals">Open approvals inbox</a></div>
          </section>
        </div>
        """;

    return PortalUi.AppPage(context, "Overview", principal.Identity?.Name ?? "MateMCP user", body);
}).RequireAuthorization();
app.MapPost("/api/enrollment/start", async (EnrollmentStart request, ControlPlaneDbContext db) =>
{
    var recoverAgentId = string.IsNullOrWhiteSpace(request.RecoverAgentId) ? null : request.RecoverAgentId.Trim();
    if (recoverAgentId is not null && !IsAgentId(recoverAgentId)) return Results.BadRequest(new { error = "invalid_agent_id" });
    var raw = Token(32); var code = CreateUserCode();
    db.Enrollments.Add(new EnrollmentSession { DeviceCodeHash = Hash(raw), UserCode = code, DeviceName = request.Name.Trim(), Platform = EncodeEnrollmentPlatform(request.Platform.Trim(), recoverAgentId), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) });
    await db.SaveChangesAsync();
    return Results.Ok(new { deviceCode = raw, userCode = code, verificationUri = publicUrl + "/device", verificationUriComplete = publicUrl + "/device?code=" + code, interval = 3, expiresIn = 600 });
});
app.MapGet("/device", (HttpContext context, ClaimsPrincipal principal, string? code) =>
{
    var body = $"""
        {PortalUi.PageHeading("Enrollment", "Add a device", "Enter the short code shown by MateMCP Desktop on the computer you want to connect.")}
        <section class="portal-section">
          <div class="form-card">
            <div class="form-card-grid">
              <form class="auth-form" method="post" action="/device/approve">
                <div class="form-field">
                  <label for="device-code">Enrollment code</label>
                  <input id="device-code" name="code" value="{PortalUi.H(code ?? "")}" autocomplete="one-time-code" spellcheck="false" required autofocus>
                  <p class="field-hint">Codes are short-lived. Enter the code exactly as shown by MateMCP Desktop.</p>
                </div>
                <button class="button button-primary" type="submit">Approve device</button>
              </form>
              <aside class="help-panel">
                <strong>How enrollment works</strong>
                <ol>
                  <li>Open MateMCP Desktop on the computer.</li>
                  <li>Start device enrollment and copy its code.</li>
                  <li>Enter the code here to bind that device to your account.</li>
                </ol>
              </aside>
            </div>
          </div>
        </section>
        """;
    return PortalUi.AppPage(context, "Add a device", principal.Identity?.Name ?? "MateMCP user", body, "device");
}).RequireAuthorization();

app.MapPost("/device/approve", async (HttpContext context, ClaimsPrincipal principal, ControlPlaneDbContext db) =>
{
    var form = await context.Request.ReadFormAsync();
    var code = form["code"].ToString().Trim().ToUpperInvariant();
    var enrollment = await db.Enrollments.SingleOrDefaultAsync(x => x.UserCode == code && !x.Consumed && x.ApprovedByUserId == null);
    if (enrollment is null || enrollment.ExpiresAt <= DateTimeOffset.UtcNow)
    {
        var body = $"""
            {PortalUi.PageHeading("Enrollment", "Add a device", "Enter the short code shown by MateMCP Desktop on the computer you want to connect.")}
            <section class="portal-section"><div class="form-card">
              {PortalUi.Alert("That enrollment code is invalid or has expired.")}
              <div class="form-card-grid">
                <form class="auth-form" method="post" action="/device/approve">
                  <div class="form-field"><label for="device-code">Enrollment code</label><input id="device-code" name="code" value="{PortalUi.H(code)}" autocomplete="one-time-code" spellcheck="false" required autofocus></div>
                  <button class="button button-primary" type="submit">Try again</button>
                </form>
                <aside class="help-panel"><strong>Need a new code?</strong><p>Return to MateMCP Desktop and start enrollment again, then enter the new code here.</p></aside>
              </div>
            </div></section>
            """;
        return PortalUi.AppPage(context, "Add a device", principal.Identity?.Name ?? "MateMCP user", body, "device", StatusCodes.Status400BadRequest);
    }

    var (_, recoverAgentId) = DecodeEnrollmentPlatform(enrollment.Platform);
    var userId = UserId(principal);
    if (recoverAgentId is not null)
    {
        var recoverable = await db.Agents.AnyAsync(x => x.PublicId == recoverAgentId && x.OwnerId == userId && !x.IsRevoked);
        if (!recoverable) return Results.Forbid();
    }

    enrollment.ApprovedByUserId = userId;
    await db.SaveChangesAsync();
    var action = recoverAgentId is null ? "Device approved" : "Device recovery approved";
    var successBody = $"""
        {PortalUi.PageHeading("Enrollment", action, "The local Agent can now complete its secure enrollment flow.")}
        <section class="portal-section"><div class="form-card">
          {PortalUi.Alert($"{enrollment.DeviceName} can now finish setup. You may return to MateMCP Desktop.", "success")}
          <a class="button button-secondary" href="/dashboard">Back to overview</a>
        </div></section>
        """;
    return PortalUi.AppPage(context, action, principal.Identity?.Name ?? "MateMCP user", successBody, "device");
}).RequireAuthorization();
app.MapPost("/api/enrollment/token", async (EnrollmentToken request, ControlPlaneDbContext db) =>
{
    var deviceCodeHash = Hash(request.DeviceCode); var enrollment = await db.Enrollments.SingleOrDefaultAsync(x => x.DeviceCodeHash == deviceCodeHash); if (enrollment is null || enrollment.ExpiresAt <= DateTimeOffset.UtcNow) return Results.BadRequest(new { error = "expired_token" }); if (enrollment.ApprovedByUserId is null) return Results.StatusCode(428); if (enrollment.Consumed) return Results.BadRequest(new { error = "invalid_grant" });
    if (!await db.Users.AnyAsync(x => x.Id == enrollment.ApprovedByUserId.Value && !x.IsDisabled)) return Results.Forbid();
    var (platform, recoverAgentId) = DecodeEnrollmentPlatform(enrollment.Platform);
    var credential = Token(48);
    AgentDevice agent;
    if (recoverAgentId is not null)
    {
        agent = await db.Agents.SingleOrDefaultAsync(x => x.PublicId == recoverAgentId && x.OwnerId == enrollment.ApprovedByUserId.Value && !x.IsRevoked) ?? throw new InvalidOperationException("Approved recovery target is no longer available.");
        agent.CredentialHash = Hash(credential); agent.Name = enrollment.DeviceName; agent.Platform = platform; enrollment.Consumed = true;
        db.AuditEvents.Add(new AuditEvent { UserId = agent.OwnerId, AgentDeviceId = agent.Id, EventType = "agent.credential_rotated", Detail = agent.Name });
    }
    else
    {
        agent = new AgentDevice { PublicId = "agt_" + Token(18), OwnerId = enrollment.ApprovedByUserId.Value, Name = enrollment.DeviceName, Platform = platform, CredentialHash = Hash(credential) }; enrollment.Consumed = true; db.Agents.Add(agent); db.AuditEvents.Add(new AuditEvent { UserId = agent.OwnerId, AgentDeviceId = agent.Id, EventType = "agent.enrolled", Detail = agent.Name });
    }
    await db.SaveChangesAsync();
    return Results.Ok(new { agentId = agent.PublicId, credential, relayUrl, mcpUrl = $"{relayUrl}/mcp/{agent.PublicId}" });
});

app.MapMethods("/connect/authorize", ["GET", "POST"], async (HttpContext context, ControlPlaneDbContext db) =>
{
    var request = context.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("OIDC request unavailable."); if (context.User.Identity?.IsAuthenticated != true) return Results.Redirect("/login?returnUrl=" + Uri.EscapeDataString(context.Request.Path + context.Request.QueryString));
    var resources = request.GetResources(); if (resources.Length != 1 || !TryAgentId(resources[0], relayUrl, out var publicId)) return Results.BadRequest(new { error = OpenIddictConstants.Errors.InvalidTarget });
    var userId = UserId(context.User); var agent = await db.Agents.SingleOrDefaultAsync(x => x.PublicId == publicId && x.OwnerId == userId && !x.IsRevoked); if (agent is null) return Results.Forbid();
    var scopes = MateMCP.Api.OAuthScopePolicy.FilterAuthorizedScopes(request.GetScopes(), agent.AllowedScopes); var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, OpenIddictConstants.Claims.Name, ClaimTypes.Role);
    identity.AddClaim(OpenIddictConstants.Claims.Subject, userId.ToString()); identity.AddClaim(OpenIddictConstants.Claims.Name, context.User.Identity!.Name!); identity.AddClaim("agent_id", agent.PublicId); identity.SetScopes(scopes); identity.SetResources(resources[0], relayUrl); identity.SetDestinations(_ => [OpenIddictConstants.Destinations.AccessToken]);
    return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
});
app.MapPost("/connect/register", async (HttpContext context, IOpenIddictApplicationManager manager) =>
{
    var r = await context.Request.ReadFromJsonAsync<ClientRegistration>(); if (r?.RedirectUris is null || r.RedirectUris.Length == 0 || r.RedirectUris.Any(x => !Uri.TryCreate(x, UriKind.Absolute, out _))) return Results.BadRequest(new { error = "invalid_client_metadata" });
    var d = new OpenIddictApplicationDescriptor { ClientId = Guid.NewGuid().ToString("N"), ClientType = OpenIddictConstants.ClientTypes.Public, ConsentType = OpenIddictConstants.ConsentTypes.Implicit, DisplayName = string.IsNullOrWhiteSpace(r.ClientName) ? "MCP Client" : r.ClientName }; foreach (var uri in r.RedirectUris) d.RedirectUris.Add(new Uri(uri));
    d.Permissions.UnionWith([OpenIddictConstants.Permissions.Endpoints.Authorization, OpenIddictConstants.Permissions.Endpoints.Token, OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode, OpenIddictConstants.Permissions.GrantTypes.RefreshToken, OpenIddictConstants.Permissions.ResponseTypes.Code, OpenIddictConstants.Permissions.Prefixes.Scope + "mcp:read", OpenIddictConstants.Permissions.Prefixes.Scope + "mcp:write", OpenIddictConstants.Permissions.Prefixes.Scope + "mcp:shell", OpenIddictConstants.Permissions.Prefixes.Scope + OpenIddictConstants.Scopes.OfflineAccess]); await manager.CreateAsync(d);
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Pragma = "no-cache";
    return Results.Json(new { client_id = d.ClientId, client_name = d.DisplayName, redirect_uris = r.RedirectUris, token_endpoint_auth_method = "none", grant_types = new[] { "authorization_code", "refresh_token" }, response_types = new[] { "code" } }, statusCode: StatusCodes.Status201Created);
});

app.MapPost("/internal/agents/authenticate", async (HttpContext c, AgentAuthentication r, ControlPlaneDbContext db) =>
{
    if (!Internal(c, internalKey)) return Results.Unauthorized(); var credentialHash = Hash(r.Credential); var agent = await db.Agents.Include(x => x.Owner).SingleOrDefaultAsync(x => x.PublicId == r.AgentId && x.CredentialHash == credentialHash && !x.IsRevoked && x.Owner != null && !x.Owner.IsDisabled); if (agent is null) return Results.Unauthorized(); agent.LastSeenAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); return Results.Ok(new { agentId = agent.PublicId, ownerId = agent.OwnerId, scopes = agent.AllowedScopes.Split(' ') });
});
app.MapPost("/internal/agents/offline", async (HttpContext c, AgentOffline r, ControlPlaneDbContext db) =>
{
    if (!Internal(c, internalKey)) return Results.Unauthorized();
    var agent = await db.Agents.SingleOrDefaultAsync(x => x.PublicId == r.AgentId && !x.IsRevoked);
    if (agent is null) return Results.NotFound();
    if (agent.LastSeenAt <= r.LastSeenAt)
    {
        agent.LastSeenAt = DateTimeOffset.MinValue;
        await db.SaveChangesAsync();
    }
    return Results.Ok();
});
app.MapPost("/internal/agents/authorize", async (HttpContext c, AgentAuthorization r, ControlPlaneDbContext db) =>
{
    if (!Internal(c, internalKey)) return Results.Unauthorized(); var agent = await db.Agents.Include(x => x.Owner).SingleOrDefaultAsync(x => x.PublicId == r.AgentId && !x.IsRevoked && x.Owner != null && !x.Owner.IsDisabled); if (agent is null || agent.OwnerId.ToString() != r.UserId) return Results.Forbid(); return MateMCP.Api.OAuthScopePolicy.AreGrantedAgentScopesAllowed(r.Scopes, agent.AllowedScopes) ? Results.Ok() : Results.Forbid();
});
app.MapPost("/api/agents/{agentId}/approvals", async (string agentId, HttpContext c, NewApproval r, ControlPlaneDbContext db) =>
{
    var agent = await AuthenticateAgent(c, agentId, db); if (agent is null) return Results.Unauthorized(); var approval = new ApprovalRequest { AgentDeviceId = agent.Id, Capability = r.Capability, Target = r.Target, Summary = r.Summary, OperationHash = Hash(r.Capability + "\n" + r.Target + "\n" + r.Summary), ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(r.ExpiresIn, 15, 600)) }; db.Approvals.Add(approval); await db.SaveChangesAsync(); return Results.Ok(new { id = approval.Id, status = approval.Status, expiresAt = approval.ExpiresAt });
});
app.MapGet("/api/agents/{agentId}/approvals/{id:guid}", async (string agentId, Guid id, HttpContext c, ControlPlaneDbContext db) =>
{
    var agent = await AuthenticateAgent(c, agentId, db); if (agent is null) return Results.Unauthorized(); var a = await db.Approvals.SingleOrDefaultAsync(x => x.Id == id && x.AgentDeviceId == agent.Id); if (a is null) return Results.NotFound(); if (a.Status == "pending" && a.ExpiresAt <= DateTimeOffset.UtcNow) { a.Status = "expired"; await db.SaveChangesAsync(); } return Results.Ok(new { a.Status });
});

app.Run();

static async Task EnsureDatabaseAsync(IServiceProvider services, IConfiguration c)
{
    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
    await db.Database.EnsureCreatedAsync();

    var email = c["MateMCP:BootstrapAdminEmail"]?.Trim();
    var password = c["MateMCP:BootstrapAdminPassword"];
    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        return;

    var normalizedEmail = email.ToUpperInvariant();
    var existing = await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail);
    if (existing is not null)
    {
        if (!existing.IsAdmin)
        {
            existing.IsAdmin = true;
            await db.SaveChangesAsync();
        }
        return;
    }

    var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<UserAccount>>();
    var admin = new UserAccount
    {
        Email = email,
        NormalizedEmail = normalizedEmail,
        PasswordHash = "pending",
        IsAdmin = true
    };
    admin.PasswordHash = hasher.HashPassword(admin, password);
    db.Users.Add(admin);
    await db.SaveChangesAsync();
}
static ClaimsPrincipal CreateCookiePrincipal(UserAccount u)
{
    var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
    identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()));
    identity.AddClaim(new Claim(ClaimTypes.Name, u.Email));
    if (u.IsAdmin) identity.AddClaim(new Claim(ClaimTypes.Role, "admin"));
    return new ClaimsPrincipal(identity);
}
static async Task SignInAsync(HttpContext c, UserAccount u) => await c.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, CreateCookiePrincipal(u));
static Guid UserId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);
static string Token(int bytes) => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(bytes));
static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
static string CreateUserCode() { const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; var b = RandomNumberGenerator.GetBytes(8); return string.Concat(b.Select((x, i) => chars[x % chars.Length] + (i == 3 ? "-" : ""))); }
static bool Internal(HttpContext c, string expected) => SecretEquals(c.Request.Headers["X-MateMCP-Internal-Key"].ToString(), expected);
static async Task<AgentDevice?> AuthenticateAgent(HttpContext c, string id, ControlPlaneDbContext db) { var auth = c.Request.Headers.Authorization.ToString(); if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null; var credentialHash = Hash(auth[7..]); return await db.Agents.Include(x => x.Owner).SingleOrDefaultAsync(x => x.PublicId == id && x.CredentialHash == credentialHash && !x.IsRevoked && x.Owner != null && !x.Owner.IsDisabled); }
static bool SecretEquals(string a, string b) { var x = Encoding.UTF8.GetBytes(a); var y = Encoding.UTF8.GetBytes(b); return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y); }
static bool TryAgentId(string resource, string relay, out string id) { var prefix = relay + "/mcp/"; id = resource.StartsWith(prefix, StringComparison.Ordinal) ? resource[prefix.Length..] : ""; return IsAgentId(id); }
static bool IsAgentId(string id) => id.StartsWith("agt_", StringComparison.Ordinal) && id.Length > 4 && !id.Contains('/');
const string RecoveryMarker = "\nMATEMCP_RECOVER:";
static string EncodeEnrollmentPlatform(string platform, string? recoverAgentId) => recoverAgentId is null ? platform : platform + RecoveryMarker + recoverAgentId;
static (string Platform, string? RecoverAgentId) DecodeEnrollmentPlatform(string value) { var index = value.LastIndexOf(RecoveryMarker, StringComparison.Ordinal); if (index < 0) return (value, null); var agentId = value[(index + RecoveryMarker.Length)..]; return IsAgentId(agentId) ? (value[..index], agentId) : (value, null); }
static bool RequiresAntiforgery(PathString path) =>
    path == "/login" ||
    path == "/register" ||
    path == "/logout" ||
    path == "/device/approve" ||
    path.StartsWithSegments("/devices") ||
    path.StartsWithSegments("/approvals") ||
    path.StartsWithSegments("/admin");
static bool IsLocal(string s) => !string.IsNullOrEmpty(s) && s[0] == '/' && (s.Length == 1 || s[1] != '/' && s[1] != '\\');
static string SafeReturnUrl(string? value) => !string.IsNullOrWhiteSpace(value) && IsLocal(value) ? value : "/dashboard";
static RSA LoadOrCreateRsaKey(string path) { var rsa = RSA.Create(3072); if (File.Exists(path)) { rsa.ImportFromPem(File.ReadAllText(path)); return rsa; } File.WriteAllText(path, rsa.ExportPkcs8PrivateKeyPem()); return rsa; }

sealed record EnrollmentStart(string Name, string Platform, string? RecoverAgentId = null);
sealed record EnrollmentToken(string DeviceCode);
sealed record AgentAuthentication(string AgentId, string Credential);
sealed record AgentOffline(string AgentId, DateTimeOffset LastSeenAt);
sealed record AgentAuthorization(string AgentId, string UserId, string[] Scopes);
sealed record NewApproval(string Capability, string Target, string Summary, int ExpiresIn = 120);
sealed record ClientRegistration([property: System.Text.Json.Serialization.JsonPropertyName("client_name")] string? ClientName, [property: System.Text.Json.Serialization.JsonPropertyName("redirect_uris")] string[]? RedirectUris);