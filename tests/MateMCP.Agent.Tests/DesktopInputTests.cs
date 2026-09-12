using MateMCP.Agent.Desktop;
using MateMCP.Agent.Tools;

namespace MateMCP.Agent.Tests;

public sealed class DesktopInputTests
{
    [Theory]
    [InlineData("left", "left")]
    [InlineData(" RIGHT ", "right")]
    [InlineData("Middle", "middle")]
    public void Mouse_buttons_are_normalized(string input, string expected)
        => Assert.Equal(expected, DesktopInputService.NormalizeButton(input));

    [Theory]
    [InlineData("return", "ENTER")]
    [InlineData("escape", "ESC")]
    [InlineData("control", "CTRL")]
    [InlineData("option", "ALT")]
    [InlineData("command", "CMD")]
    [InlineData("meta", "CMD")]
    [InlineData("ArrowLeft", "LEFT")]
    [InlineData("f12", "F12")]
    [InlineData("a", "A")]
    [InlineData("7", "7")]
    public void Keys_are_normalized(string input, string expected)
        => Assert.Equal(expected, DesktopInputService.NormalizeKey(input));

    [Fact]
    public void Unsupported_keys_are_rejected()
        => Assert.Throws<ArgumentException>(() => DesktopInputService.NormalizeKey("DO_SOMETHING_MAGIC"));

    [Fact]
    public void Desktop_input_tools_are_published_in_the_MCP_catalog()
    {
        var expected = new[]
        {
            "mouse_move",
            "mouse_click",
            "mouse_drag",
            "mouse_scroll",
            "keyboard_type",
            "keyboard_press",
            "keyboard_shortcut",
            "window_focus"
        };
        foreach (var tool in expected) Assert.Contains(tool, McpToolCatalog.Names);
    }
}
