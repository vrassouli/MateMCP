using System.Net;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Projects;
using MateMCP.Agent.Security;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables(prefix: "MATEMCP_");
builder.Services.Configure<MateOptions>(builder.Configuration.GetSection(MateOptions.SectionName));
var options = builder.Configuration.GetSection(MateOptions.SectionName).Get<MateOptions>() ?? new MateOptions();

if (string.IsNullOrWhiteSpace(options.AccessToken))
    throw new InvalidOperationException("Mate:AccessToken must be configured before starting MateMCP.");
if (!options.AllowInsecureHttp && string.IsNullOrWhiteSpace(options.CertificatePath))
    throw new InvalidOperationException("HTTPS is required. Configure Mate:CertificatePath or explicitly set Mate:AllowInsecureHttp=true for local development.");

builder.WebHost.ConfigureKestrel(kestrel =>
{
    var ip = IPAddress.Parse(options.BindAddress);
    kestrel.Listen(ip, options.Port, listen =>
    {
        if (!options.AllowInsecureHttp)
            listen.UseHttps(options.CertificatePath!, options.CertificatePassword);
    });
    kestrel.Limits.MaxRequestBodySize = 4 * 1024 * 1024;
});

builder.Services.AddSingleton<ProjectRegistry>();
builder.Services.AddSingleton<AuditLog>();
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
app.MapMcp("/mcp").RequireRateLimiting("mcp");
app.Run();
