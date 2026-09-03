using MateMCP.Relay;

namespace MateMCP.Relay.Tests;

public sealed class OAuthDiscoveryTests
{
    [Theory]
    [InlineData("https://api.matemcp.com", "https://api.matemcp.com/")]
    [InlineData("https://api.matemcp.com/", "https://api.matemcp.com/")]
    [InlineData("https://auth.example.com/oauth", "https://auth.example.com/oauth/")]
    public void NormalizeIssuer_ProducesStableIssuerIdentifier(string input, string expected)
        => Assert.Equal(expected, OAuthDiscovery.NormalizeIssuer(input));

    [Fact]
    public void Challenge_AdvertisesResourceMetadataAndAuthenticationError()
    {
        var challenge = OAuthDiscovery.BearerChallenge(
            "https://relay.matemcp.com/",
            "agt_test",
            ["mcp:read", "mcp:write"]);

        Assert.Contains("error=\"invalid_token\"", challenge);
        Assert.Contains("error_description=\"Authentication required\"", challenge);
        Assert.Contains("resource_metadata=\"https://relay.matemcp.com/.well-known/oauth-protected-resource/mcp/agt_test\"", challenge);
        Assert.Contains("scope=\"mcp:read mcp:write\"", challenge);
    }
}
