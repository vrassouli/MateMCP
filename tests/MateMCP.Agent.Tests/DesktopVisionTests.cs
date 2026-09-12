using MateMCP.Agent.Desktop;
using MateMCP.Agent.Tools;

namespace MateMCP.Agent.Tests;

public sealed class DesktopVisionTests
{
    [Theory]
    [InlineData("screen", "screen")]
    [InlineData(" SCREEN ", "screen")]
    [InlineData("window", "window")]
    [InlineData("region", "region")]
    public void Capture_target_is_normalized(string input, string expected)
        => Assert.Equal(expected, DesktopVisionService.NormalizeTarget(input));

    [Theory]
    [InlineData("")]
    [InlineData("desktop")]
    [InlineData("monitor")]
    [InlineData("mouse")]
    public void Unknown_capture_target_is_rejected(string target)
        => Assert.Throws<ArgumentException>(() => DesktopVisionService.NormalizeTarget(target));

    [Fact]
    public void Vision_tools_are_published_in_the_MCP_catalog()
    {
        Assert.Contains("screen_list", McpToolCatalog.Names);
        Assert.Contains("window_list", McpToolCatalog.Names);
        Assert.Contains("screen_capture", McpToolCatalog.Names);
    }

    [Fact]
    public async Task Unsupported_platform_fails_explicitly()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) return;
        var service = new DesktopVisionService();
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => service.ListScreensAsync());
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => service.ListWindowsAsync());
    }
}
