using System.Net;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Projects;
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

if (string.IsNullOrWhiteSpace(options.AccessToken) || options.AccessToken == "change-me-before-exposing")
    throw new InvalidOperationException($"A secure Mate:AccessToken is required. Edit '{userConfigPath}'.");
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

builder.Services.AddSingleton<ProjectRegistry>();
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<ApprovalService>();
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
app.MapGet("/health", () => Results.Ok(new { service = "MateMCP", status = "ok" }));
app.MapGet("/status", (HttpContext context) =>
{
    if (!IsLoopback(context)) return Results.NotFound();
    return Results.Ok(new
    {
        service = "MateMCP",
        endpoint = $"{(options.AllowInsecureHttp ? "http" : "https")}://{options.BindAddress}:{options.Port}/mcp",
        configuration = userConfigPath,
        projects = options.Projects.Select(p => p.Name).ToArray(),
        shellApproval = options.RequireShellApproval
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
app.MapMcp("/mcp").RequireRateLimiting("mcp");
app.Run();

static bool IsLoopback(HttpContext context)
{
    var remote = context.Connection.RemoteIpAddress;
    return remote is not null && IPAddress.IsLoopback(remote);
}
