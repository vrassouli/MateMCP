using System.Security.Claims;
using OpenIddict.Abstractions;

namespace MateMCP.Relay;

internal static class RouteAwareAuthorization
{
    public static string? GetGrantAuthorizationId(ClaimsPrincipal principal, bool stableEndpoint)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (!stableEndpoint)
            return null;

        // New stable-endpoint tokens carry the explicit MateMCP marker.
        // Tokens issued between grant management and the marker rollout only
        // carry OpenIddict's standard authorization ID. Legacy per-Agent
        // routes must never forward that generic ID to grant validation.
        return principal.FindFirstValue("matemcp_authorization_id") ?? principal.GetAuthorizationId();
    }
}
