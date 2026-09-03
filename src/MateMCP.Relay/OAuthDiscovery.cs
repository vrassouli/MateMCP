namespace MateMCP.Relay;

public static class OAuthDiscovery
{
    public static string NormalizeIssuer(string authorizationServerUrl)
    {
        if (!Uri.TryCreate(authorizationServerUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Authorization server URL must be an absolute HTTP(S) URL.", nameof(authorizationServerUrl));

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        var value = builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return value + "/";
    }

    public static string ProtectedResourceMetadataUrl(string publicBaseUrl, string deviceId)
        => $"{publicBaseUrl.TrimEnd('/')}/.well-known/oauth-protected-resource/mcp/{deviceId}";

    public static string BearerChallenge(string publicBaseUrl, string deviceId, IEnumerable<string> scopes)
        => $"Bearer error=\"invalid_token\", error_description=\"Authentication required\", resource_metadata=\"{ProtectedResourceMetadataUrl(publicBaseUrl, deviceId)}\", scope=\"{string.Join(' ', scopes)}\"";
}
