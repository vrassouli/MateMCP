namespace MateMCP.Relay;

public sealed class RelayOptions
{
    public const string SectionName = "Relay";
    public string PublicBaseUrl { get; set; } = "https://relay.matemcp.com";
    public string AuthorizationServerUrl { get; set; } = "https://api.matemcp.com";
    public string ControlPlaneUrl { get; set; } = "https://api.matemcp.com";
    public string InternalApiKey { get; set; } = "change-me";
    public string[] OAuthScopes { get; set; } = ["mcp:read", "mcp:write", "mcp:shell"];
    public int MaxBodyBytes { get; set; } = 4 * 1024 * 1024;
    public int RequestTimeoutSeconds { get; set; } = 120;
}
