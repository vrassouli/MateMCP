using System.Security.Cryptography;
using System.Text;
using MateMCP.Agent.Configuration;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Security;

public sealed class BearerTokenMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IOptions<MateOptions> options)
    {
        if (!context.Request.Path.StartsWithSegments("/mcp"))
        {
            await next(context);
            return;
        }

        var expected = options.Value.AccessToken;
        var authorization = context.Request.Headers.Authorization.ToString();
        var supplied = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(expected) || !FixedEquals(expected, supplied))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await next(context);
    }

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
