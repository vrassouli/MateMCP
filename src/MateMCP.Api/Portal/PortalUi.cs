using System.Net;
using System.Text.RegularExpressions;
using MateMCP.Api.ExternalAuth;
using Microsoft.AspNetCore.Antiforgery;

namespace MateMCP.Api.Portal;

public static class PortalUi
{
    public static IResult AuthPage(
        HttpContext context,
        bool registration,
        string returnUrl,
        string? error = null,
        string email = "",
        int statusCode = StatusCodes.Status200OK)
    {
        NoStore(context);
        if (string.IsNullOrWhiteSpace(error))
            error = context.Request.Query["externalError"].ToString();

        var title = registration ? "Ready to keep working?" : "Keep your AI working";
        var eyebrow = registration ? "Bring your own AI" : "Pick up where you left off";
        var subtitle = registration
            ? "Create an account and connect the AI chat you already use to the computers you already own."
            : "Sign in and give your available AI chat a controlled path back to your enrolled devices.";
        var submit = registration ? "Create account" : "Sign in";
        var action = registration ? "/register" : "/login";
        var alternateLabel = registration ? "Already have an account?" : "New to MateMCP?";
        var alternateAction = registration ? "Sign in" : "Create an account";
        var alternatePath = registration ? "/login" : "/register";
        var passwordAutocomplete = registration ? "new-password" : "current-password";
        var passwordHint = registration
            ? "<p class=\"field-hint\" id=\"password-hint\">Use at least 10 characters.</p>"
            : "";
        var describedBy = registration ? " aria-describedby=\"password-hint\"" : "";
        var errorMarkup = string.IsNullOrWhiteSpace(error)
            ? ""
            : $"<div class=\"form-alert\" role=\"alert\"><span class=\"alert-icon\">!</span><span>{H(error)}</span></div>";

        var externalCatalog = context.RequestServices.GetService<ExternalAuthCatalog>();
        var externalMode = registration ? "register" : "login";
        var externalMarkup = externalCatalog is null || externalCatalog.Providers.Count == 0
            ? ""
            : $"<div class=\"external-auth\"><div class=\"external-provider-grid\">{string.Join("", externalCatalog.Providers.Select(provider => $"<a class=\"external-provider-button\" href=\"/auth/external/{Uri.EscapeDataString(provider.Id)}?mode={externalMode}&amp;returnUrl={Uri.EscapeDataString(returnUrl)}\"><span class=\"provider-mark provider-{H(provider.Id)}\" aria-hidden=\"true\">{ProviderMark(provider.Id)}</span><span>Continue with {H(provider.DisplayName)}</span></a>"))}</div><div class=\"auth-divider\"><span>or continue with email</span></div></div>";

        var body = $"""
            <div class="auth-layout">
              <section class="auth-story" aria-label="About MateMCP">
                <a class="brand brand-auth" href="https://matemcp.com/" aria-label="MateMCP home">
                  <img src="/portal/mark.svg?v=1" width="38" height="38" alt="">
                  <span>MateMCP</span>
                </a>
                <div class="auth-story-copy">
                  <div class="eyebrow"><span class="status-dot"></span> Keep your AI working</div>
                  <h1>Built-in agent hit a limit?<br><span>Keep going.</span></h1>
                  <p>When your AI chat is still available, MateMCP can connect that conversation to your own computers so the work does not have to stop.</p>
                </div>
                <div class="auth-trust-list">
                  <div><span class="trust-icon">01</span><div><strong>Make more of your existing AI plan</strong><small>When chat remains available, MateMCP gives it local tools to keep useful work moving.</small></div></div>
                  <div><span class="trust-icon">02</span><div><strong>Bring your own AI</strong><small>Use the same enrolled machines from ChatGPT, Claude, Grok, or another compatible MCP client.</small></div></div>
                  <div><span class="trust-icon">03</span><div><strong>Your machines, your rules</strong><small>Keep access scoped, approval-aware, and backed by local credential handling.</small></div></div>
                </div>
                <a class="back-link" href="https://matemcp.com/">
                  <span aria-hidden="true">←</span> Back to matemcp.com
                </a>
              </section>

              <main class="auth-main">
                <div class="auth-mobile-brand">
                  <a class="brand" href="https://matemcp.com/"><img src="/portal/mark.svg?v=1" width="34" height="34" alt=""><span>MateMCP</span></a>
                </div>
                <section class="auth-card" aria-labelledby="auth-title">
                  <div class="auth-heading">
                    <div class="eyebrow">{H(eyebrow)}</div>
                    <h2 id="auth-title">{H(title)}</h2>
                    <p>{H(subtitle)}</p>
                  </div>
                  {errorMarkup}
                  {externalMarkup}
                  <form class="auth-form" method="post" action="{action}">
                    <input type="hidden" name="returnUrl" value="{H(returnUrl)}">
                    <div class="form-field">
                      <label for="email">Email address</label>
                      <input id="email" name="email" type="email" value="{H(email)}" autocomplete="email" inputmode="email" required autofocus>
                    </div>
                    <div class="form-field">
                      <div class="label-row"><label for="password">Password</label>{(registration ? "" : "<span class=\"field-meta\">10+ characters</span>")}</div>
                      <input id="password" name="password" type="password" autocomplete="{passwordAutocomplete}" minlength="{(registration ? "10" : "1")}" required{describedBy}>
                      {passwordHint}
                    </div>
                    <button class="button button-primary button-block" type="submit">
                      <span>{submit}</span><span aria-hidden="true">→</span>
                    </button>
                  </form>
                  <div class="auth-alternate">
                    <span>{alternateLabel}</span>
                    <a href="{alternatePath}?returnUrl={Uri.EscapeDataString(returnUrl)}">{alternateAction}</a>
                  </div>
                </section>
                <p class="auth-footnote">By continuing, you are accessing the MateMCP control plane for your own enrolled devices.</p>
              </main>
            </div>
            """;

        return Html(context, title, body, statusCode, "auth-page");
    }

    public static IResult AppPage(
        HttpContext context,
        string title,
        string email,
        string body,
        string active = "overview",
        int statusCode = StatusCodes.Status200OK)
    {
        NoStore(context);

        string Nav(string key, string href, string icon, string label)
            => $"<a class=\"portal-nav-link{(active == key ? " is-active" : "")}\" href=\"{href}\"><span class=\"nav-icon\" aria-hidden=\"true\">{icon}</span><span>{label}</span></a>";

        var shell = $"""
            <div class="portal-app">
              <header class="portal-header">
                <div class="portal-header-inner">
                  <a class="brand" href="/dashboard" aria-label="MateMCP Control home">
                    <img src="/portal/mark.svg?v=1" width="34" height="34" alt="">
                    <span>MateMCP</span>
                    <span class="brand-product">Control</span>
                  </a>
                  <button class="portal-nav-toggle" type="button" aria-label="Open navigation" aria-expanded="false" aria-controls="portal-nav" data-portal-nav-toggle>
                    <span></span><span></span><span></span>
                  </button>
                  <nav class="portal-nav" id="portal-nav" aria-label="Account navigation" data-portal-nav>
                    {Nav("overview", "/dashboard", "⌂", "Overview")}
                    {Nav("devices", "/devices", "◇", "Devices")}
                    {Nav("approvals", "/approvals", "!", "Approvals")}
                    {Nav("account", "/account", "◎", "Account")}
                    {(context.User.IsInRole("admin") ? Nav("admin", "/admin", "A", "Admin") : "")}
                    {Nav("device", "/device", "+", "Add device")}
                    <a class="portal-nav-link" href="https://matemcp.com/"><span class="nav-icon" aria-hidden="true">↗</span><span>Product site</span></a>
                    <form class="portal-nav-signout" method="post" action="/logout"><button class="portal-nav-link portal-nav-button" type="submit"><span class="nav-icon" aria-hidden="true">↪</span><span>Sign out</span></button></form>
                  </nav>
                  <div class="account-cluster">
                    <div class="account-avatar" aria-hidden="true">{Initial(email)}</div>
                    <div class="account-copy"><strong>{H(email)}</strong><span>{(context.User.IsInRole("admin") ? "Administrator" : "Signed in")}</span></div>
                    <form method="post" action="/logout"><button class="button button-ghost button-compact" type="submit">Sign out</button></form>
                  </div>
                </div>
              </header>
              <main class="portal-main">
                <div class="portal-shell">{body}</div>
              </main>
            </div>
            <script src="/portal/portal.js?v=301-2" defer></script>
            """;

        return Html(context, title, shell, statusCode, "portal-page");
    }

    public static string PageHeading(string eyebrow, string title, string description, string? actions = null)
        => $"""
           <section class="page-heading">
             <div>
               <div class="eyebrow">{H(eyebrow)}</div>
               <h1>{H(title)}</h1>
               <p>{H(description)}</p>
             </div>
             {(string.IsNullOrWhiteSpace(actions) ? "" : $"<div class=\"page-actions\">{actions}</div>")}
           </section>
           """;

    public static string StatusBadge(string status)
    {
        var normalized = status.ToLowerInvariant();
        var css = normalized switch
        {
            "online" or "allowed" or "active" or "enabled" => "status-positive",
            "pending" => "status-warning",
            "revoked" or "denied" or "disabled" => "status-negative",
            _ => "status-neutral"
        };

        return $"<span class=\"status-badge {css}\"><span class=\"status-dot-small\"></span>{H(status)}</span>";
    }

    public static string EmptyState(string title, string description, string? action = null)
        => $"""
           <div class="empty-state">
             <div class="empty-icon" aria-hidden="true">◇</div>
             <strong>{H(title)}</strong>
             <p>{H(description)}</p>
             {(string.IsNullOrWhiteSpace(action) ? "" : $"<div class=\"empty-action\">{action}</div>")}
           </div>
           """;

    public static string Alert(string message, string kind = "error")
        => $"<div class=\"inline-alert inline-alert-{H(kind)}\" role=\"alert\"><span aria-hidden=\"true\">!</span><div>{H(message)}</div></div>";

    private static string ProviderMark(string provider)
        => provider.ToLowerInvariant() switch
        {
            "google" => "G",
            "microsoft" => "M",
            "github" => "GH",
            "apple" => "●",
            _ => provider[..Math.Min(2, provider.Length)].ToUpperInvariant()
        };

    public static string H(string value) => WebUtility.HtmlEncode(value);

    private static char Initial(string email)
        => string.IsNullOrWhiteSpace(email) ? 'M' : char.ToUpperInvariant(email.Trim()[0]);

    private static void NoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }

    private static IResult Html(HttpContext context, string title, string body, int statusCode, string bodyClass)
    {
        var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
        var tokens = antiforgery.GetAndStoreTokens(context);
        var tokenInput = $"<input type=\"hidden\" name=\"{H(tokens.FormFieldName)}\" value=\"{H(tokens.RequestToken ?? string.Empty)}\">";
        body = Regex.Replace(
            body,
            "<form(?=[^>]*\\bmethod=\"post\")[^>]*>",
            match => match.Value + tokenInput,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var html = $"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <meta name="theme-color" content="#07111f">
              <meta name="robots" content="noindex,nofollow">
              <link rel="icon" href="/portal/mark.svg?v=1" type="image/svg+xml">
              <link rel="stylesheet" href="/portal/portal.css?v=301-2">
              <script src="/portal/analytics.js?v=1" defer></script>
              <title>{H(title)} · MateMCP</title>
            </head>
            <body class="{bodyClass}">
              <a class="skip-link" href="#main-content">Skip to content</a>
              {body.Replace("<main class=\"auth-main\">", "<main class=\"auth-main\" id=\"main-content\">", StringComparison.Ordinal)
                   .Replace("<main class=\"portal-main\">", "<main class=\"portal-main\" id=\"main-content\">", StringComparison.Ordinal)}
            </body>
            </html>
            """;

        return Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
    }
}