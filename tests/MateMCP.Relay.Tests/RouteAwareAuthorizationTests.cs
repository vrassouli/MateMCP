using System.Security.Claims;
using OpenIddict.Abstractions;
using MateMCP.Relay;

namespace MateMCP.Relay.Tests;

public sealed class RouteAwareAuthorizationTests
{
    [Fact]
    public void LegacyPerAgentRoute_DoesNotForwardGenericOpenIddictAuthorizationId()
    {
        var identity = new ClaimsIdentity("Bearer");
        identity.SetAuthorizationId("generic-openiddict-authorization");
        identity.AddClaim(new Claim("matemcp_authorization_id", "explicit-matemcp-grant"));
        var principal = new ClaimsPrincipal(identity);

        Assert.Null(RouteAwareAuthorization.GetGrantAuthorizationId(principal, stableEndpoint: false));
    }

    [Fact]
    public void StableEndpoint_PreMarkerToken_UsesGenericOpenIddictAuthorizationId()
    {
        var identity = new ClaimsIdentity("Bearer");
        identity.SetAuthorizationId("pre-marker-stable-grant");
        var principal = new ClaimsPrincipal(identity);

        Assert.Equal(
            "pre-marker-stable-grant",
            RouteAwareAuthorization.GetGrantAuthorizationId(principal, stableEndpoint: true));
    }

    [Fact]
    public void StableEndpoint_CurrentToken_PrefersExplicitMateMcpGrantClaim()
    {
        var identity = new ClaimsIdentity("Bearer");
        identity.SetAuthorizationId("generic-openiddict-authorization");
        identity.AddClaim(new Claim("matemcp_authorization_id", "explicit-matemcp-grant"));
        var principal = new ClaimsPrincipal(identity);

        Assert.Equal(
            "explicit-matemcp-grant",
            RouteAwareAuthorization.GetGrantAuthorizationId(principal, stableEndpoint: true));
    }

    [Fact]
    public void StableEndpoint_WithoutAnyAuthorizationId_PassesNull()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity("Bearer"));

        Assert.Null(RouteAwareAuthorization.GetGrantAuthorizationId(principal, stableEndpoint: true));
    }
}
