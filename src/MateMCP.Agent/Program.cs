using System.Net;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Desktop;
using MateMCP.Agent.Diagnostics;
using MateMCP.Agent.Memory;
using MateMCP.Agent.Projects;
using MateMCP.Agent.Relay;
using MateMCP.Agent.Security;
using MateMCP.Agent.Tools;
using Microsoft.AspNetCore.RateLimiting;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);
var userConfigPath = ConfigurationBootstrap.EnsureUserConfiguration();
var agentLogStore = new AgentLogStore(Path.Combine(Path.GetDirectoryName(userConfigPath)!, "agent-logs.jsonl"));
builder.Logging.AddProvider(new AgentLogProvider(agentLogStore));
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
builder.Services.AddSingleton(agentLogStore);
builder.Services.AddSingleton(new LocalAccessCredential(localAccessToken));
builder.Services.AddSingleton(new EnrollmentStateStore(userConfigPath));
builder.Services.AddSingleton<DeviceManagementService>();
builder.Services.AddSingleton<UserSecretStore>();
builder.Services.AddSingleton<ICredentialStore>(sp => sp.GetRequiredService<UserSecretStore>());
builder.Services.AddSingleton<InteractiveShellSessionManager>();
builder.Services.AddSingleton<ProjectRegistry>();
builder.Services.AddSingleton<ProjectConfigurationService>();
builder.Services.AddSingleton(sp => new SkillMemoryStore(sp.GetRequiredService<ProjectRegistry>(), Path.Combine(Path.GetDirectoryName(userConfigPath)!, "skills-memory.json")));
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<ApprovalPolicyStore>();
builder.Services.AddSingleton<CredentialInjectionRateLimiter>();
builder.Services.AddSingleton<CompanionNotificationPresence>();
builder.Services.AddSingleton<LocalNotificationService>();
builder.Services.AddSingleton<ApprovalService>();
builder.Services.AddSingleton<IApprovalService>(sp => sp.GetRequiredService<ApprovalService>());
builder.Services.AddSingleton<AgentActivityGate>();
builder.Services.AddSingleton<DesktopUpdateSettingsStore>();
builder.Services.AddSingleton<BackgroundDesktopUpdateService>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<BackgroundDesktopUpdateService>(sp => sp.GetRequiredService<BackgroundDesktopUpdateService>());
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
    var agentVersion = typeof(ProjectRegistry).Assembly.GetName().Version?.ToString() ?? "unknown";
    return Results.Ok(new
    {
        service = "MateMCP",
        version = agentVersion,
        endpoint = $"{(current.AllowInsecureHttp ? "http" : "https")}://{current.BindAddress}:{current.Port}/mcp",
        management = $"http://127.0.0.1:{current.Port}/ui",
        managementApi = new { revision = 2, capabilities = new[] { "projects-stable-id", "skills-memory", "desktop-update", "agent-logs" } },
        configuration = userConfigPath,
        projects = projects.All.Select(p => p.Name).ToArray(),
        shellApproval = current.RequireShellApproval,
        interactiveSessions = sessions.ActiveSessionCount,
        mcpTools = new { count = McpToolCatalog.Names.Count, revision = McpToolCatalog.Revision, names = McpToolCatalog.Names },
        relay = new { current.Relay.Enabled, current.Relay.Url, current.Relay.DeviceId, current.Relay.EnrollmentSuppressed },
        credentials = OperatingSystem.IsMacOS() ? "macOS Keychain" : OperatingSystem.IsWindows() ? "Windows Credential Manager" : "platform credential store"
    });
});

app.MapGet("/devices", async (HttpContext context, DeviceManagementService devices, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try { return Results.Ok(await devices.GetStatusAsync(ct)); }
    catch (HttpRequestException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway); }
});
app.MapPost("/devices/enrollment/enable", (HttpContext context, DeviceManagementService devices) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try
    {
        devices.EnableEnrollment();
        return Results.Ok(new { status = "enabled", restartRequired = true });
    }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
});
app.MapDelete("/devices/current", async (HttpContext context, DeviceManagementService devices, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try
    {
        await devices.SignOutCurrentAsync(ct);
        return Results.Ok(new { status = "signed-out", restartRequired = true });
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or PlatformNotSupportedException)
    {
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
    }
});
app.MapDelete("/devices/{deviceId}", async (string deviceId, HttpContext context, DeviceManagementService devices, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try
    {
        await devices.RevokeOtherAsync(deviceId, ct);
        return Results.Ok(new { status = "revoked" });
    }
    catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict); }
    catch (HttpRequestException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway); }
});

app.MapGet("/desktop-update", async (HttpContext context, BackgroundDesktopUpdateService updates, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    return Results.Ok(await updates.GetStatusAsync(ct));
});
app.MapPut("/desktop-update/auto", async (DesktopAutoUpdateUpdate update, HttpContext context, DesktopUpdateSettingsStore settings, BackgroundDesktopUpdateService updates, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    await settings.SetAutoUpdateEnabledAsync(update.Enabled, ct);
    updates.RequestCheck();
    return Results.Ok(await updates.GetStatusAsync(ct));
});

app.MapGet("/skills-memory", async (HttpContext context, SkillMemoryStore store, string? scope, string? project, string? type, string? text, bool? includeDisabled, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try { return Results.Ok(await store.SearchAsync(scope, project, type, text, includeDisabled ?? false, ct)); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapGet("/skills-memory/{id}", async (string id, HttpContext context, SkillMemoryStore store, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try { return Results.Ok(await store.GetAsync(id, ct)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
});
app.MapPost("/skills-memory", async (SkillMemoryUpdate update, HttpContext context, SkillMemoryStore store, AuditLog audit, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try
    {
        var item = await store.CreateAsync(update, ct);
        await audit.WriteAsync("memory.create", item.Id, $"{item.Source}:{item.Scope}:{item.Project ?? "global"}", ct);
        return Results.Created($"/skills-memory/{item.Id}", item);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapPut("/skills-memory/{id}", async (string id, SkillMemoryUpdate update, HttpContext context, SkillMemoryStore store, AuditLog audit, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try
    {
        var item = await store.UpdateAsync(id, update, ct);
        await audit.WriteAsync("memory.update", item.Id, $"{item.Source}:{item.Scope}:{item.Project ?? "global"}", ct);
        return Results.Ok(item);
    }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Results.BadRequest(new { error = ex.Message }); }
});
app.MapDelete("/skills-memory/{id}", async (string id, HttpContext context, SkillMemoryStore store, AuditLog audit, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    var removed = await store.DeleteAsync(id, ct);
    if (!removed) return Results.NotFound();
    await audit.WriteAsync("memory.delete", id, "user", ct);
    return Results.Ok(new { status = "removed" });
});

app.MapGet("/logs", (HttpContext context, AgentLogStore logs, long? after, int? limit, string? level, string? text) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    LogLevel? minimumLevel = null;
    if (!string.IsNullOrWhiteSpace(level))
    {
        if (!Enum.TryParse<LogLevel>(level, true, out var parsed) || parsed == LogLevel.None)
            return Results.BadRequest(new { error = "Unknown log level." });
        minimumLevel = parsed;
    }
    var entries = logs.Read(Math.Max(0, after ?? 0), limit ?? 500, minimumLevel, text);
    return Results.Ok(new { entries, cursor = logs.LatestId });
});
app.MapDelete("/logs", async (HttpContext context, AgentLogStore logs, AuditLog audit, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    logs.Clear();
    await audit.WriteAsync("diagnostics.logs", "agent", "cleared", ct);
    return Results.Ok(new { status = "cleared" });
});

app.MapPost("/companion/notifications/ready", (HttpContext context, CompanionNotificationPresence presence) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    presence.MarkReady();
    return Results.Ok(new { status = "ready" });
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
app.MapGet("/audit", async (HttpContext context, AuditLog audit, int? limit, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try { return Results.Ok(await audit.ReadAsync(limit ?? 200, from, to, ct)); }
    catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
});
app.MapDelete("/audit", async (HttpContext context, AuditLog audit, DateTimeOffset? before, CancellationToken ct) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    if (before is null) return Results.BadRequest(new { error = "The before cutoff is required." });
    var deleted = await audit.DeleteBeforeAsync(before.Value, ct);
    return Results.Ok(new { deleted });
});

app.MapGet("/shell/sessions", (HttpContext context, InteractiveShellSessionManager sessions) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    return Results.Ok(sessions.List());
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
public sealed record DesktopAutoUpdateUpdate(bool Enabled);
