using OpenIddict.Abstractions;

namespace MateMCP.Api;

public static class OAuthScopePolicy
{
    public static string[] FilterAuthorizedScopes(IEnumerable<string> requestedScopes, string allowedAgentScopes)
    {
        var allowedCapabilities = ParseAgentScopes(allowedAgentScopes);

        return requestedScopes
            .Where(scope => IsProtocolScope(scope) || allowedCapabilities.Contains(scope))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    public static bool AreGrantedAgentScopesAllowed(IEnumerable<string> grantedScopes, string allowedAgentScopes)
    {
        var allowedCapabilities = ParseAgentScopes(allowedAgentScopes);

        return grantedScopes
            .Where(scope => !IsProtocolScope(scope))
            .All(allowedCapabilities.Contains);
    }

    public static bool IsProtocolScope(string scope)
        => string.Equals(scope, OpenIddictConstants.Scopes.OfflineAccess, StringComparison.Ordinal);

    private static HashSet<string> ParseAgentScopes(string allowedAgentScopes)
        => allowedAgentScopes
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
}
