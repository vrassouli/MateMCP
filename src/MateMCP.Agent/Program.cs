using System.Net;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Desktop;
using MateMCP.Agent.Projects;
using MateMCP.Agent.Relay;
using MateMCP.Agent.Security;
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
if (string.IsNullOrWhiteSpace(localAccessToken))
    throw new InvalidOperationException("A secure local MateMCP access credential could not be created.");
if (!IPAddress.TryParse(options.BindAddress, out var bindIp))
    throw new InvalidOperationException("Mate:BindAddress must be an IP address.");
if (options.AllowInsecureHttp && !IPAddress.IsLoopback(bindIp))
    throw new InvalidOperationException("Insecure HTTP is only allowed on a loopback address. Use HTTPS for direct exposure, or keep MateMCP on 127.0.0.1 behind a local TLS reverse proxy such as Caddy.");
if (!options.AllowInsecureHttp && string.IsNullOrWhiteSpace(options.CertificatePath))
    throw new InvalidOperationException("HTTPS is required. Configure Mate:CertificatePath or use loopback HTTP behind a trusted local TLS reverse proxy.");

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(bindIp, options.Port, listen =>
    {
        if (!options.AllowInsecureHttp)
            listen.UseHttps(options.CertificatePath!, options.CertificatePassword);
    });
    kestrel.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
});

builder.Services.AddSingleton(credentialStore);
builder.Services.AddSingleton(new LocalAccessCredential(localAccessToken));
builder.Services.AddSingleton<ProjectRegistry>();
builder.Services.AddSingleton<ProjectConfigurationService>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<LocalNotificationService>();
builder.Services.AddSingleton<ApprovalService>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<EnrollmentService>();
builder.Services.AddHostedService<RelayConnector>();
builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("mcp", limiter =>
{
    limiter.PermitLimit = 120;
    limiter.Window = TimeSpan.FromMinutes(1);
    limiter.QueueLimit = 0;
}));
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
    .WithToolsFromAssembly();

var app = builder.Build();
app.UseRateLimiter();
app.UseMiddleware<BearerTokenMiddleware>();

app.MapGet("/", (HttpContext context) =>
    IsLoopback(context)
        ? Results.Redirect("/ui", permanent: false)
        : Results.NotFound());
app.MapGet("/health", () => Results.Ok(new { service = "MateMCP", status = "ok" }));
app.MapGet("/ui", (HttpContext context) =>
    IsLoopback(context)
        ? Results.Content(AgentUi.Html, "text/html; charset=utf-8")
        : Results.NotFound());
app.MapGet("/status", (HttpContext context, Microsoft.Extensions.Options.IOptionsMonitor<MateOptions> currentOptions, ProjectRegistry projects) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    var current = currentOptions.CurrentValue;
    return Results.Ok(new
    {
        service = "MateMCP",
        endpoint = $"{(current.AllowInsecureHttp ? "http" : "https")}://{current.BindAddress}:{current.Port}/mcp",
        management = $"http://127.0.0.1:{current.Port}/ui",
        configuration = userConfigPath,
        projects = projects.All.Select(p => p.Name).ToArray(),
        shellApproval = current.RequireShellApproval,
        relay = new { current.Relay.Enabled, current.Relay.Url, current.Relay.DeviceId },
        credentials = OperatingSystem.IsMacOS() ? "macOS Keychain" : OperatingSystem.IsWindows() ? "Windows Credential Manager" : "platform credential store"
    });
});

app.MapGet("/approvals", (HttpContext context, ApprovalService approvals) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    return Results.Ok(approvals.GetPending());
});
app.MapPost("/approvals/{id}/allow", (string id, HttpContext context, ApprovalService approvals) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    return approvals.Decide(id, true) ? Results.Ok(new { status = "allowed" }) : Results.NotFound();
});
app.MapPost("/approvals/{id}/deny", (string id, HttpContext context, ApprovalService approvals) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    return approvals.Decide(id, false) ? Results.Ok(new { status = "denied" }) : Results.NotFound();
});

app.MapGet("/projects", (HttpContext context, ProjectRegistry projects) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    return Results.Ok(projects.All.OrderBy(p => p.Name).ToArray());
});
app.MapPost("/projects", (ProjectUpdate update, HttpContext context, ProjectConfigurationService projects) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try { return Results.Ok(projects.Add(update)); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DirectoryNotFoundException)
    { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
});
app.MapPut("/projects/{name}", (string name, ProjectUpdate update, HttpContext context, ProjectConfigurationService projects) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    try { return Results.Ok(projects.Update(name, update)); }
    catch (KeyNotFoundException) { return Results.NotFound(); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DirectoryNotFoundException)
    { return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest); }
});
app.MapDelete("/projects/{name}", (string name, HttpContext context, ProjectConfigurationService projects) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    return projects.Remove(name) ? Results.Ok(new { status = "removed" }) : Results.NotFound();
});

app.MapMcp("/mcp").RequireRateLimiting("mcp");
app.Run();

static bool IsLoopback(HttpContext context)
{
    var remote = context.Connection.RemoteIpAddress;
    return remote is not null && IPAddress.IsLoopback(remote);
}
