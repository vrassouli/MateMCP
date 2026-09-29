using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MateMCP.Web.Tests;

public sealed class PublicSiteTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public PublicSiteTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Home_page_exposes_product_story_and_security_metadata()
    {
        var response = await _client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Your agent hit a limit?", html, StringComparison.Ordinal);
        Assert.Contains("id=\"product\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"security\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"how-it-works\"", html, StringComparison.Ordinal);
        Assert.Contains("https://api.matemcp.com/register", html, StringComparison.Ordinal);
        Assert.Contains("https://api.matemcp.com/login", html, StringComparison.Ordinal);
        Assert.Contains("https://github.com/vrassouli/MateMCP#install--upgrade-matemcp-desktop", html, StringComparison.Ordinal);
        Assert.Contains("rel=\"canonical\" href=\"https://matemcp.com/\"", html, StringComparison.Ordinal);
        Assert.Contains("property=\"og:title\"", html, StringComparison.Ordinal);
        Assert.Contains("<script src=\"/analytics.js\" defer></script>", html, StringComparison.Ordinal);

        Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var contentTypeOptions));
        Assert.Contains("nosniff", contentTypeOptions);
        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var csp));
        Assert.Contains(csp, value => value.Contains("frame-ancestors 'none'", StringComparison.Ordinal));
        Assert.Contains(csp, value => value.Contains("script-src 'self' https://static.cloudflareinsights.com https://*.googletagmanager.com", StringComparison.Ordinal));
        Assert.Contains(csp, value => value.Contains("connect-src 'self' https://cloudflareinsights.com https://*.google-analytics.com", StringComparison.Ordinal));
        Assert.True(response.Headers.TryGetValues("Permissions-Policy", out var permissions));
        Assert.Contains(permissions, value => value.Contains("camera=()", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/site.css", "text/css")]
    [InlineData("/site.js", "text/javascript")]
    [InlineData("/analytics.js", "text/javascript")]
    [InlineData("/brand/mark.svg", "image/svg+xml")]
    public async Task Static_assets_are_served_with_long_lived_cache_headers(string path, string contentType)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith(contentType, response.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=604800", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stylesheet_keeps_responsive_and_accessibility_contracts()
    {
        var css = await _client.GetStringAsync("/site.css");

        Assert.Contains(":focus-visible", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 820px)", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 520px)", css, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", css, StringComparison.Ordinal);
        Assert.Contains("min-height: 44px", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forwarded_https_emits_hsts_for_reverse_proxy_requests()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Strict-Transport-Security", out var hsts));
        Assert.Contains(hsts, value => value.Contains("max-age=31536000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Health_endpoint_reports_public_web_service()
    {
        var response = await _client.GetAsync("/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("MateMCP.Web", body, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"ok\"", body, StringComparison.Ordinal);
    }
}
