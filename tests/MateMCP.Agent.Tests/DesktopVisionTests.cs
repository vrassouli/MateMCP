using System.Text.Json;
using MateMCP.Agent.Desktop;
using MateMCP.Agent.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

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
    public void Image_content_factory_round_trips_through_the_MCP_protocol_serializer()
    {
        byte[] pngBytes =
        [
            137, 80, 78, 71, 13, 10, 26, 10,
            0, 0, 0, 13, 73, 72, 68, 82,
            0, 0, 0, 1, 0, 0, 0, 1
        ];
        var result = new CallToolResult
        {
            Content = [ImageContentBlock.FromBytes(pngBytes, "image/png")]
        };

        var json = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);
        using (var document = JsonDocument.Parse(json))
        {
            var data = document.RootElement.GetProperty("content")[0].GetProperty("data").GetString();
            Assert.Equal(Convert.ToBase64String(pngBytes), data);
        }

        var roundTrip = JsonSerializer.Deserialize<CallToolResult>(json, McpJsonUtilities.DefaultOptions);
        Assert.NotNull(roundTrip);
        var image = Assert.IsType<ImageContentBlock>(Assert.Single(roundTrip.Content));
        Assert.Equal(pngBytes, image.DecodedData.ToArray());
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
