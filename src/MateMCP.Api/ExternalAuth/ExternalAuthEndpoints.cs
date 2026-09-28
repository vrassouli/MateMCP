using System.Net;
using System.Security.Claims;
using MateMCP.Api.Data;
using MateMCP.Api.Portal;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace MateMCP.Api.ExternalAuth;

public static class ExternalAuthEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/auth/external/{provider}", (
            string provider,
            HttpContext context,
            ExternalAuthCatalog catalog) =>
        {
            if (!catalog.TryGet(provider, out var definition))
                return Results.NotFound();

            var returnUrl = SafeReturnUrl(context.Request.Query["returnUrl"].ToString());
            var mode = string.Equals(context.Request.Query["mode"], "register", StringComparison.Ordinal)
                ? "register"
                : "login";
            var intent = string.Equals(context.Request.Query["intent"], "link", StringComparison.Ordinal)
                ? "link"
                : "signin";

            var properties = new AuthenticationProperties
            {
                RedirectUri = "/auth/external/complete"
            };
            properties.Items["provider"] = definition.Id;
            properties.Items["returnUrl"] = returnUrl;
            properties.Items["mode"] = mode;
            properties.Items["intent"] = intent;

            if (intent == "link")
            {
                if (context.User.Identity?.IsAuthenticated != true)
                    return Results.Redirect("/login?returnUrl=" + Uri.EscapeDataString("/account"));

                properties.Items["linkUserId"] = UserId(context.User).ToString();
            }

            return Results.Challenge(properties, [definition.Scheme]);
        });

        app.MapGet("/auth/external/complete", async (
            HttpContext context,
            ExternalAuthCatalog catalog,
            ExternalAuthAccountService accountService) =>
        {
            var external = await context.AuthenticateAsync(ExternalAuthRegistration.TemporaryCookieScheme);
            if (!external.Succeeded || external.Principal is null)
                return RedirectWithExternalError("login", "/dashboard", "External sign-in session expired. Please try again.");

            var properties = external.Properties;
            var providerId = PropertyItem(properties, "provider");
            var mode = string.Equals(PropertyItem(properties, "mode"), "register", StringComparison.Ordinal)
                ? "register"
                : "login";
            var returnUrl = SafeReturnUrl(PropertyItem(properties, "returnUrl"));
            var intent = PropertyItem(properties, "intent");

            if (!catalog.TryGet(providerId, out var provider))
            {
                await context.SignOutAsync(ExternalAuthRegistration.TemporaryCookieScheme);
                return RedirectWithExternalError(mode, returnUrl, "This external sign-in provider is no longer enabled.");
            }

            Guid? linkUserId = null;
            if (string.Equals(intent, "link", StringComparison.Ordinal))
            {
                if (!Guid.TryParse(PropertyItem(properties, "linkUserId"), out var requestedUserId) ||
                    context.User.Identity?.IsAuthenticated != true ||
                    UserId(context.User) != requestedUserId)
                {
                    await context.SignOutAsync(ExternalAuthRegistration.TemporaryCookieScheme);
                    return Results.Redirect("/login?returnUrl=" + Uri.EscapeDataString("/account"));
                }

                linkUserId = requestedUserId;
            }

            var providerKey = ExternalProviderKey(provider, external.Principal);
            var email = ExternalEmail(provider, external.Principal);
            var verified = IsEmailVerified(provider, external.Principal, email);

            var completion = await accountService.CompleteAsync(
                provider.Id,
                providerKey ?? string.Empty,
                email,
                verified,
                linkUserId,
                context.RequestAborted);

            await context.SignOutAsync(ExternalAuthRegistration.TemporaryCookieScheme);

            if (!completion.Succeeded)
            {
                if (linkUserId is not null)
                    return Results.Redirect("/account?externalError=" + Uri.EscapeDataString(completion.Error!));

                return RedirectWithExternalError(mode, returnUrl, completion.Error!);
            }

            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                CreateCookiePrincipal(completion.User!));

            return Results.Redirect(linkUserId is not null
                ? "/account?externalConnected=" + Uri.EscapeDataString(provider.DisplayName)
                : returnUrl);
        });

        app.MapGet("/account", async (
            HttpContext context,
            ControlPlaneDbContext db,
            ExternalAuthCatalog catalog) =>
        {
            if (context.User.Identity?.IsAuthenticated != true)
                return Results.Redirect("/login?returnUrl=" + Uri.EscapeDataString("/account"));

            var userId = UserId(context.User);
            var user = await db.Users
                .AsNoTracking()
                .Include(x => x.ExternalLogins)
                .SingleOrDefaultAsync(x => x.Id == userId && !x.IsDisabled);

            if (user is null)
                return Results.Redirect("/login");

            var error = context.Request.Query["externalError"].ToString();
            var connected = context.Request.Query["externalConnected"].ToString();
            var message = !string.IsNullOrWhiteSpace(error)
                ? $"<div class=\"inline-alert\" role=\"alert\"><span>!</span><span>{H(error)}</span></div>"
                : !string.IsNullOrWhiteSpace(connected)
                    ? $"<div class=\"inline-alert inline-alert-success\" role=\"status\"><span>✓</span><span>{H(connected)} is now connected to your MateMCP account.</span></div>"
                    : string.Empty;

            var rows = catalog.Providers.Count == 0
                ? "<p class=\"empty-copy\">No external sign-in providers are enabled for this deployment.</p>"
                : string.Join("", catalog.Providers.Select(provider =>
                {
                    var login = user.ExternalLogins.FirstOrDefault(x =>
                        string.Equals(x.Provider, provider.Id, StringComparison.OrdinalIgnoreCase));

                    return login is not null
                        ? $"""
                           <div class="provider-account-row">
                             <div><strong>{H(provider.DisplayName)}</strong><small>Connected as {H(login.Email)}</small></div>
                             <span class="status-pill status-online">Connected</span>
                           </div>
                           """
                        : $"""
                           <div class="provider-account-row">
                             <div><strong>{H(provider.DisplayName)}</strong><small>Use this identity to sign in to MateMCP.</small></div>
                             <a class="button button-secondary button-compact" href="/auth/external/{Uri.EscapeDataString(provider.Id)}?intent=link&amp;returnUrl=%2Faccount">Connect</a>
                           </div>
                           """;
                }));

            var body = $"""
                <section class="page-heading">
                  <div>
                    <span class="eyebrow">Account security</span>
                    <h1>Sign-in methods</h1>
                    <p>Connect external identities without replacing your existing password sign-in.</p>
                  </div>
                </section>
                {message}
                <section class="panel account-provider-panel">
                  <div class="panel-heading">
                    <div><h2>External providers</h2><p>Only providers enabled by this deployment are shown.</p></div>
                  </div>
                  <div class="provider-account-list">{rows}</div>
                </section>
                """;

            return PortalUi.AppPage(context, "Account", user.Email, body, "account");
        });
    }

    private static string? PropertyItem(AuthenticationProperties? properties, string key)
        => properties is not null && properties.Items.TryGetValue(key, out var value) ? value : null;

    private static string? ExternalProviderKey(
        ExternalAuthProviderDefinition provider,
        ClaimsPrincipal principal)
        => provider.Protocol == ExternalAuthProtocol.OpenIdConnect
            ? principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            : principal.FindFirstValue(ClaimTypes.NameIdentifier);

    private static string? ExternalEmail(
        ExternalAuthProviderDefinition provider,
        ClaimsPrincipal principal)
    {
        var email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email");
        if (string.IsNullOrWhiteSpace(email) && string.Equals(provider.Id, "microsoft", StringComparison.Ordinal))
            email = principal.FindFirstValue("preferred_username");

        return string.IsNullOrWhiteSpace(email) || !email.Contains('@', StringComparison.Ordinal)
            ? null
            : email.Trim();
    }

    private static bool IsEmailVerified(
        ExternalAuthProviderDefinition provider,
        ClaimsPrincipal principal,
        string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return false;

        if (!provider.RequiresVerifiedEmailClaim)
            return true;

        var value =
            principal.FindFirstValue(ExternalAuthRegistration.VerifiedEmailClaim) ??
            principal.FindFirstValue("email_verified");

        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "1", StringComparison.Ordinal);
    }

    private static ClaimsPrincipal CreateCookiePrincipal(UserAccount user)
    {
        var identity = new ClaimsIdentity(
            CookieAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.Email));
        if (user.IsAdmin)
            identity.AddClaim(new Claim(ClaimTypes.Role, "admin"));
        return new ClaimsPrincipal(identity);
    }

    private static IResult RedirectWithExternalError(string mode, string returnUrl, string error)
        => Results.Redirect(
            $"/{mode}?returnUrl={Uri.EscapeDataString(returnUrl)}&externalError={Uri.EscapeDataString(error)}");

    private static Guid UserId(ClaimsPrincipal principal)
        => Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static bool IsLocal(string value)
        => !string.IsNullOrEmpty(value) &&
           value[0] == '/' &&
           (value.Length == 1 || value[1] != '/' && value[1] != '\\');

    private static string SafeReturnUrl(string? value)
        => !string.IsNullOrWhiteSpace(value) && IsLocal(value)
            ? value
            : "/dashboard";

    private static string H(string value) => WebUtility.HtmlEncode(value);
}
