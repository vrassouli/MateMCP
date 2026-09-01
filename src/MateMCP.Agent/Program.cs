using System.Net;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Desktop;
using MateMCP.Agent.Projects;
using MateMCP.Agent.Relay;
using MateMCP.Agent.Security;
using MateMCP.Agent.Tools;
using Microsoft.AspNetCore.RateLimiting;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);
var userConfigPath = ConfigurationBootstrap.EnsureUserConfiguration();
builder.Configuration.AddJsonFile(userConfigPath, optional: false, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables(prefix: "MATEMCP_");
builder.Services.Configure<MateOptions>(builder.Configuration.GetSection(MateOptions.SectionName));
var options = builder.Configuration.GetSection(MateOptions.SectionName).Get<MateOptions>() ?? new MateOptions();

var credentialStore = new AgentCredentialStore();
var localAccessToken = await credentialStore.ResolveLocalAccessTokenAsync(options.AccessToken, userConfigPath, CancellationToken.None);
if (string.IsNullOrWhiteSpace(localAccessToken)) throw new InvalidOperationException("A secure local MateMCP access credential could not be created.");
if (!IPAddress.TryParse(options.BindAddress, out var bindIp)) throw new InvalidOperationException("Mate:BindAddress must be an IP address.");
if (options.AllowInsecureHttp && !IPAddress.IsLoopback(bindIp)) throw new InvalidOperationException("Insecure HTTP is only allowed on a loopback address. Use HTTPS for direct exposure, or keep MateMCP on 127.0.0.1 behind a local TLS reverse proxy such as Caddy.");
if (!options.AllowInsecureHttp && string.IsNullOrWhiteSpace(options.CertificatePath)) throw new InvalidOperationException("HTTPS is required. Configure Mate:CertificatePath or use loopback HTTP behind a trusted local TLS reverse proxy.");

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(bindIp, options.Port, listen => { if (!options.AllowInsecureHttp) listen.UseHttps(options.CertificatePath!, options.CertificatePassword); });
    kestrel.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
});

builder.Services.AddSingleton(credentialStore);
builder.Services.AddSingleton(new LocalAccessCredential(localAccessToken));
builder.Services.AddSingleton<UserSecretStore>();
builder.Services.AddSingleton<ICredentialStore>(sp => sp.GetRequiredService<UserSecretStore>());
builder.Services.AddSingleton<InteractiveShellSessionManager>();
builder.Services.AddSingleton<ProjectRegistry>();
builder.Services.AddSingleton<ProjectConfigurationService>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<ApprovalPolicyStore>();
builder.Services.AddSingleton<CredentialInjectionRateLimiter>();
builder.Services.AddSingleton<LocalNotificationService>();
builder.Services.AddSingleton<ApprovalService>();
builder.Services.AddSingleton<IApprovalService>(sp => sp.GetRequiredService<ApprovalService>());
builder.Services.AddHttpClient();
builder.Services.AddHostedService<EnrollmentService>();
builder.Services.AddHostedService<RelayConnector>();
builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("mcp", limiter => { limiter.PermitLimit = 120; limiter.Window = TimeSpan.FromMinutes(1); limiter.QueueLimit = 0; }));
builder.Services.AddMcpServer().WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless).WithToolsFromAssembly();

var app = builder.Build();
app.UseRateLimiter(); app.UseMiddleware<BearerTokenMiddleware>();
app.MapGet("/", (HttpContext context) => IsLoopback(context) ? Results.Redirect("/ui", permanent: false) : Results.NotFound());
app.MapGet("/health", () => Results.Ok(new { service = "MateMCP", status = "ok" }));
app.MapGet("/ui", (HttpContext context) => IsLoopback(context) ? Results.Content(AgentUi.Html, "text/html; charset=utf-8") : Results.NotFound());
app.MapGet("/status", (HttpContext context, Microsoft.Extensions.Options.IOptionsMonitor<MateOptions> currentOptions, ProjectRegistry projects, InteractiveShellSessionManager sessions) =>
{
    if (!IsLoopback(context)) return Results.NotFound(); var current = currentOptions.CurrentValue;
    return Results.Ok(new { service = "MateMCP", endpoint = $"{(current.AllowInsecureHttp ? "http" : "https")}://{current.BindAddress}:{current.Port}/mcp", management = $"http://127.0.0.1:{current.Port}/ui", configuration = userConfigPath, projects = projects.All.Select(p => p.Name).ToArray(), shellApproval = current.RequireShellApproval, interactiveSessions = sessions.ActiveSessionCount, relay = new { current.Relay.Enabled, current.Relay.Url, current.Relay.DeviceId }, credentials = OperatingSystem.IsMacOS() ? "macOS Keychain" : OperatingSystem.IsWindows() ? "Windows Credential Manager" : "platform credential store" });
});

app.MapGet("/approvals", (HttpContext context, ApprovalService approvals) => IsLoopback(context) ? Results.Ok(approvals.GetPending()) : Results.NotFound());
app.MapPost("/approvals/{id}/{decision}", (string id, string decision, HttpContext context, ApprovalService approvals) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    var parsed = decision switch { "allow" => ApprovalDecision.AllowOnce, "allow-session" => ApprovalDecision.AllowSession, "allow-always" => ApprovalDecision.AllowAlways, "deny" => ApprovalDecision.Deny, _ => (ApprovalDecision?)null };
    if (parsed is null) return Results.BadRequest();
    return approvals.Decide(id, parsed.Value) ? Results.Ok(new { status = decision }) : Results.NotFound();
});
app.MapGet("/approval-policies", async (HttpContext context, ApprovalService approvals, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound(); return Results.Ok(await approvals.GetPoliciesAsync(ct));
});
app.MapDelete("/approval-policies", async (string capability, string target, HttpContext context, ApprovalService approvals, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound(); return await approvals.RemovePolicyAsync(capability, target, ct) ? Results.Ok(new { status = "removed" }) : Results.NotFound();
});
app.MapGet("/credential-audit", async (HttpContext context, AuditLog audit, int? limit, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    return Results.Ok(await audit.ReadCredentialUsageAsync(limit ?? 200, ct));
});
app.MapGet("/audit", async (HttpContext context, AuditLog audit, int? limit, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    return Results.Ok(await audit.ReadAsync(limit ?? 200, ct));
});

app.MapGet("/shell/sessions", async (HttpContext context, InteractiveShellSessionManager sessions, AuditLog audit, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    var history = await audit.ReadAsync(1000, ct);
    var ids = history
        .Where(x => string.Equals(x.Capability, "shell.session.start", StringComparison.Ordinal) && x.Result.StartsWith("started:", StringComparison.Ordinal))
        .Select(x => x.Result["started:".Length..])
        .Distinct(StringComparer.Ordinal)
        .ToArray();
    var active = new List<ShellSessionSnapshot>();
    foreach (var id in ids)
    {
        try { active.Add(sessions.Read(id, 0)); }
        catch (KeyNotFoundException) { }
    }
    return Results.Ok(active.OrderByDescending(x => x.LastTouched).ToArray());
});
app.MapGet("/shell/sessions/{id}", (string id, int? offset, HttpContext context, InteractiveShellSessionManager sessions) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try { return Results.Ok(sessions.Read(id, Math.Max(0, offset ?? 0))); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});
app.MapPost("/shell/sessions/{id}/input", async (string id, ShellInput update, HttpContext context, InteractiveShellSessionManager sessions, AuditLog audit, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try
    {
        await sessions.WriteAsync(id, update.Text ?? string.Empty, update.Submit, ct);
        await audit.WriteAsync("shell.session.ui-input", id, $"chars:{update.Text?.Length ?? 0};submit:{update.Submit}", ct);
        return Results.Ok(new { status = "written" });
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
});
app.MapDelete("/shell/sessions/{id}", async (string id, HttpContext context, InteractiveShellSessionManager sessions, AuditLog audit, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    var closed = sessions.Close(id);
    await audit.WriteAsync("shell.session.ui-close", id, closed ? "closed" : "not-found", ct);
    return closed ? Results.Ok(new { status = "closed" }) : Results.NotFound();
});

app.MapGet("/projects", (HttpContext context, ProjectRegistry projects) => IsLoopback(context) ? Results.Ok(projects.All.OrderBy(p => p.Name).ToArray()) : Results.NotFound());
app.MapPost("/projects", (ProjectUpdate update, HttpContext context, ProjectConfigurationService projects) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try { return Results.Ok(projects.Add(update)); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DirectoryNotFoundException) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
});
app.MapPut("/projects/{name}", (string name, ProjectUpdate update, HttpContext context, ProjectConfigurationService projects) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try { return Results.Ok(projects.Update(name, update)); } catch (KeyNotFoundException) { return Results.NotFound(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DirectoryNotFoundException) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
});
app.MapDelete("/projects/{name}", (string name, HttpContext context, ProjectConfigurationService projects) =>
{
    if (!IsLoopback(context)) return Results.NotFound(); return projects.Remove(name) ? Results.Ok(new { status = "removed" }) : Results.NotFound();
});

app.MapGet("/secrets", async (HttpContext context, UserSecretStore secrets, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try { return Results.Ok(await secrets.ListAsync(ct)); }
    catch (PlatformNotSupportedException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status501NotImplemented); }
});
app.MapPost("/secrets", async (SecretUpdate update, HttpContext context, UserSecretStore secrets, AuditLog audit, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try
    {
        await secrets.SaveAsync(update.Name, update.Value, update.Description, update.Kind, update.AllowedTools, ct);
        await audit.WriteAsync("secret.manage", update.Name, "saved", ct);
        return Results.Ok(new { update.Name, type = update.Kind.ToString(), allowedTools = update.AllowedTools });
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or PlatformNotSupportedException) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
});
app.MapDelete("/secrets/{name}", async (string name, HttpContext context, UserSecretStore secrets, AuditLog audit, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try
    {
        var removed = await secrets.DeleteAsync(name, ct);
        if (removed) await audit.WriteAsync("secret.manage", name, "removed", ct);
        return removed ? Results.Ok(new { status = "removed" }) : Results.NotFound();
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or PlatformNotSupportedException) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
});

app.MapMcp("/mcp").RequireRateLimiting("mcp");
app.Run();

static bool IsLoopback(HttpContext context) { var remote = context.Connection.RemoteIpAddress; return remote is not null && IPAddress.IsLoopback(remote); }

public sealed record SecretUpdate(string Name, string Value, string? Description, CredentialKind Kind = CredentialKind.Password,
    IReadOnlyCollection<string>? AllowedTools = null);
public sealed record ShellInput(string? Text, bool Submit = true);
