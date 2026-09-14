using MateMCP.Api;
using OpenIddict.Abstractions;

public sealed class OAuthScopePolicyTests
{
    [Fact]
    public void FilterAuthorizedScopes_PreservesOfflineAccess_AndFiltersCapabilities()
    {
        var granted = OAuthScopePolicy.FilterAuthorizedScopes(
            ["mcp:read", "mcp:shell", OpenIddictConstants.Scopes.OfflineAccess],
            "mcp:read mcp:write");

        Assert.Equal(["mcp:read", OpenIddictConstants.Scopes.OfflineAccess], granted);
    }

    [Fact]
    public void AreGrantedAgentScopesAllowed_IgnoresOfflineAccess()
    {
        var allowed = OAuthScopePolicy.AreGrantedAgentScopesAllowed(
            ["mcp:read", OpenIddictConstants.Scopes.OfflineAccess],
            "mcp:read mcp:write");

        Assert.True(allowed);
    }

    [Fact]
    public void AreGrantedAgentScopesAllowed_RejectsCapabilityEscalation()
    {
        var allowed = OAuthScopePolicy.AreGrantedAgentScopesAllowed(
            ["mcp:read", "mcp:shell", OpenIddictConstants.Scopes.OfflineAccess],
            "mcp:read mcp:write");

        Assert.False(allowed);
    }

    [Fact]
    public void UnknownScope_IsNotTreatedAsProtocolScope()
    {
        var granted = OAuthScopePolicy.FilterAuthorizedScopes(
            ["mcp:read", "custom:unknown"],
            "mcp:read");

        Assert.Equal(["mcp:read"], granted);
        Assert.False(OAuthScopePolicy.IsProtocolScope("custom:unknown"));
    }
}
