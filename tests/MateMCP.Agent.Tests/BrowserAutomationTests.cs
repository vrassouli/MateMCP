using MateMCP.Agent.Browser;
using MateMCP.Agent.Tools;

namespace MateMCP.Agent.Tests;

public sealed class BrowserAutomationTests
{
    [Theory]
    [InlineData(null, "auto")]
    [InlineData("", "auto")]
    [InlineData(" AUTO ", "auto")]
    [InlineData("google-chrome", "chrome")]
    [InlineData("edge", "msedge")]
    [InlineData("microsoft-edge", "msedge")]
    public void Browser_channels_are_normalized(string? input, string expected)
        => Assert.Equal(expected, BrowserAutomationService.NormalizeChannel(input));

    [Fact]
    public void Unsupported_browser_channel_is_rejected()
        => Assert.Throws<ArgumentException>(() => BrowserAutomationService.NormalizeChannel("firefox"));

    [Theory]
    [InlineData("http://localhost:5000/")]
    [InlineData("https://example.com/path?q=1")]
    [InlineData("http://192.168.1.2:8080/admin")]
    public void Http_urls_are_accepted(string value)
        => Assert.Equal(value, BrowserAutomationService.ValidateUrl(value).AbsoluteUri);

    [Theory]
    [InlineData("file:///tmp/index.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("about:blank")]
    [InlineData("not-a-url")]
    [InlineData("https://user:pass@example.com/")]
    public void Unsafe_or_non_http_urls_are_rejected(string value)
        => Assert.Throws<ArgumentException>(() => BrowserAutomationService.ValidateUrl(value));

    [Fact]
    public void Browser_tools_are_published_in_the_MCP_catalog()
    {
        var expected = new[]
        {
            "browser_open",
            "browser_snapshot",
            "browser_click",
            "browser_fill",
            "browser_screenshot",
            "browser_set_viewport",
            "browser_reload",
            "browser_close"
        };

        foreach (var tool in expected)
            Assert.Contains(tool, McpToolCatalog.Names);
    }
}
