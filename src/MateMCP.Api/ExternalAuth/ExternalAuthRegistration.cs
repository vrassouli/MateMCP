using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace MateMCP.Api.ExternalAuth;

public static class ExternalAuthRegistration
{
    public const string TemporaryCookieScheme = "matemcp.external.temp";
    public const string VerifiedEmailClaim = "matemcp:email_verified";

    public static AuthenticationBuilder AddMateMcpExternalProviders(
        this AuthenticationBuilder builder,
        ExternalAuthCatalog catalog)
    {
        builder.AddCookie(TemporaryCookieScheme, options =>
        {
            options.Cookie.Name = "matemcp.external";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            options.SlidingExpiration = false;
        });

        foreach (var provider in catalog.Providers)
        {
            if (provider.Protocol == ExternalAuthProtocol.OpenIdConnect)
                AddOpenIdConnect(builder, provider);
            else
                AddOAuth(builder, provider);
        }

        return builder;
    }

    private static void AddOpenIdConnect(
        AuthenticationBuilder builder,
        ExternalAuthProviderDefinition provider)
    {
        builder.AddOpenIdConnect(provider.Scheme, provider.DisplayName, options =>
        {
            options.SignInScheme = TemporaryCookieScheme;
            options.Authority = provider.Authority!;
            options.ClientId = provider.ClientId;
            options.ClientSecret = provider.ClientSecret;
            options.CallbackPath = provider.CallbackPath;
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.UsePkce = true;
            options.SaveTokens = false;
            options.MapInboundClaims = false;
            options.GetClaimsFromUserInfoEndpoint = !string.Equals(provider.Id, "apple", StringComparison.Ordinal);
            options.TokenValidationParameters.NameClaimType = "name";

            options.Scope.Clear();
            foreach (var scope in provider.Scopes)
                options.Scope.Add(scope);

            options.CorrelationCookie.SameSite = SameSiteMode.None;
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
            options.NonceCookie.SameSite = SameSiteMode.None;
            options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;

            options.Events = new OpenIdConnectEvents
            {
                OnRemoteFailure = HandleRemoteFailureAsync
            };
        });
    }

    private static void AddOAuth(
        AuthenticationBuilder builder,
        ExternalAuthProviderDefinition provider)
    {
        builder.AddOAuth(provider.Scheme, provider.DisplayName, options =>
        {
            options.SignInScheme = TemporaryCookieScheme;
            options.ClientId = provider.ClientId;
            options.ClientSecret = provider.ClientSecret;
            options.CallbackPath = provider.CallbackPath;
            options.AuthorizationEndpoint = provider.AuthorizationEndpoint!;
            options.TokenEndpoint = provider.TokenEndpoint!;
            options.UserInformationEndpoint = provider.UserInformationEndpoint!;
            options.SaveTokens = false;

            foreach (var scope in provider.Scopes)
                options.Scope.Add(scope);

            options.CorrelationCookie.SameSite = SameSiteMode.None;
            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;

            options.Events = new OAuthEvents
            {
                OnCreatingTicket = context => CreateOAuthTicketAsync(provider, context),
                OnRemoteFailure = HandleRemoteFailureAsync
            };
        });
    }

    private static async Task CreateOAuthTicketAsync(
        ExternalAuthProviderDefinition provider,
        OAuthCreatingTicketContext context)
    {
        if (!string.Equals(provider.Id, "github", StringComparison.Ordinal))
            throw new InvalidOperationException($"No OAuth profile adapter is registered for '{provider.Id}'.");

        if (string.IsNullOrWhiteSpace(context.AccessToken))
            throw new InvalidOperationException("GitHub did not return an access token.");

        using var profileRequest = CreateGitHubRequest(provider.UserInformationEndpoint!, context.AccessToken);
        using var profileResponse = await context.Backchannel.SendAsync(
            profileRequest,
            HttpCompletionOption.ResponseHeadersRead,
            context.HttpContext.RequestAborted);
        profileResponse.EnsureSuccessStatusCode();

        using var profile = JsonDocument.Parse(await profileResponse.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted));
        var root = profile.RootElement;
        if (!root.TryGetProperty("id", out var id))
            throw new InvalidOperationException("GitHub did not return an account identifier.");

        var identity = context.Identity!;
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, id.ToString()));

        if (root.TryGetProperty("login", out var login) && login.ValueKind == JsonValueKind.String)
            identity.AddClaim(new Claim(ClaimTypes.Name, login.GetString()!));

        using var emailsRequest = CreateGitHubRequest("https://api.github.com/user/emails", context.AccessToken);
        using var emailsResponse = await context.Backchannel.SendAsync(
            emailsRequest,
            HttpCompletionOption.ResponseHeadersRead,
            context.HttpContext.RequestAborted);
        emailsResponse.EnsureSuccessStatusCode();

        using var emails = JsonDocument.Parse(await emailsResponse.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted));
        var verified = emails.RootElement.EnumerateArray()
            .Where(x =>
                x.TryGetProperty("verified", out var isVerified) &&
                isVerified.ValueKind is JsonValueKind.True)
            .OrderByDescending(x =>
                x.TryGetProperty("primary", out var primary) &&
                primary.ValueKind is JsonValueKind.True)
            .Select(x => x.TryGetProperty("email", out var email) ? email.GetString() : null)
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        if (!string.IsNullOrWhiteSpace(verified))
        {
            identity.AddClaim(new Claim(ClaimTypes.Email, verified));
            identity.AddClaim(new Claim(VerifiedEmailClaim, "true"));
        }
    }

    private static HttpRequestMessage CreateGitHubRequest(string uri, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("MateMCP/1.0");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static Task HandleRemoteFailureAsync(RemoteFailureContext context)
    {
        context.HandleResponse();

        var mode = context.Properties?.Items.TryGetValue("mode", out var configuredMode) == true &&
                   string.Equals(configuredMode, "register", StringComparison.Ordinal)
            ? "register"
            : "login";
        var returnUrl = context.Properties?.Items.TryGetValue("returnUrl", out var configuredReturnUrl) == true
            ? configuredReturnUrl
            : "/dashboard";

        var message = Uri.EscapeDataString("External sign-in was cancelled or could not be completed. Please try again.");
        context.Response.Redirect($"/{mode}?returnUrl={Uri.EscapeDataString(returnUrl ?? "/dashboard")}&externalError={message}");
        return Task.CompletedTask;
    }
}
