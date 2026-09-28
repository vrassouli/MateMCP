using Microsoft.Extensions.Configuration;

namespace MateMCP.Api.ExternalAuth;

public enum ExternalAuthProtocol
{
    OpenIdConnect,
    OAuth2
}

public sealed record ExternalAuthProviderDefinition(
    string Id,
    string DisplayName,
    ExternalAuthProtocol Protocol,
    string ClientId,
    string ClientSecret,
    string CallbackPath,
    string[] Scopes,
    bool RequiresVerifiedEmailClaim,
    string? Authority = null,
    string? AuthorizationEndpoint = null,
    string? TokenEndpoint = null,
    string? UserInformationEndpoint = null)
{
    public string Scheme => "matemcp.external." + Id;
}

public sealed class ExternalAuthCatalog
{
    private readonly Dictionary<string, ExternalAuthProviderDefinition> _byId;

    private ExternalAuthCatalog(IEnumerable<ExternalAuthProviderDefinition> providers)
    {
        Providers = providers.ToArray();
        _byId = Providers.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ExternalAuthProviderDefinition> Providers { get; }

    public bool TryGet(string? id, out ExternalAuthProviderDefinition provider)
        => _byId.TryGetValue(id ?? string.Empty, out provider!);

    public static ExternalAuthCatalog FromConfiguration(IConfiguration configuration)
    {
        var root = configuration.GetSection("MateMCP:ExternalAuth:Providers");
        var providers = new List<ExternalAuthProviderDefinition>();

        AddIfEnabled(providers, root.GetSection("Google"), "google", "Google",
            ExternalAuthProtocol.OpenIdConnect, "/auth/callback/google",
            ["openid", "profile", "email"], true, authority: "https://accounts.google.com");

        AddIfEnabled(providers, root.GetSection("Microsoft"), "microsoft", "Microsoft",
            ExternalAuthProtocol.OpenIdConnect, "/auth/callback/microsoft",
            ["openid", "profile", "email"], false, authority: "https://login.microsoftonline.com/common/v2.0");

        AddIfEnabled(providers, root.GetSection("GitHub"), "github", "GitHub",
            ExternalAuthProtocol.OAuth2, "/auth/callback/github",
            ["read:user", "user:email"], true,
            authorizationEndpoint: "https://github.com/login/oauth/authorize",
            tokenEndpoint: "https://github.com/login/oauth/access_token",
            userInformationEndpoint: "https://api.github.com/user");

        AddIfEnabled(providers, root.GetSection("Apple"), "apple", "Apple",
            ExternalAuthProtocol.OpenIdConnect, "/auth/callback/apple",
            ["openid", "email", "name"], true, authority: "https://appleid.apple.com");

        var duplicateCallback = providers
            .GroupBy(x => x.CallbackPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(x => x.Count() > 1);
        if (duplicateCallback is not null)
            throw new InvalidOperationException($"External authentication callback path '{duplicateCallback.Key}' is configured more than once.");

        return new ExternalAuthCatalog(providers);
    }

    private static void AddIfEnabled(
        ICollection<ExternalAuthProviderDefinition> target,
        IConfigurationSection section,
        string id,
        string displayName,
        ExternalAuthProtocol protocol,
        string callbackPath,
        string[] scopes,
        bool requiresVerifiedEmailClaim,
        string? authority = null,
        string? authorizationEndpoint = null,
        string? tokenEndpoint = null,
        string? userInformationEndpoint = null)
    {
        if (!section.GetValue<bool>("Enabled"))
            return;

        var clientId = section["ClientId"]?.Trim();
        var clientSecret = section["ClientSecret"]?.Trim();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException($"External authentication provider '{displayName}' is enabled but ClientId/ClientSecret is missing.");

        var configuredCallback = section["CallbackPath"]?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredCallback))
        {
            if (!configuredCallback.StartsWith("/", StringComparison.Ordinal) || configuredCallback.StartsWith("//", StringComparison.Ordinal))
                throw new InvalidOperationException($"External authentication provider '{displayName}' has an invalid CallbackPath.");
            callbackPath = configuredCallback;
        }

        target.Add(new ExternalAuthProviderDefinition(
            id,
            section["DisplayName"]?.Trim() is { Length: > 0 } configuredName ? configuredName : displayName,
            protocol,
            clientId,
            clientSecret,
            callbackPath,
            scopes,
            requiresVerifiedEmailClaim,
            authority,
            authorizationEndpoint,
            tokenEndpoint,
            userInformationEndpoint));
    }
}
