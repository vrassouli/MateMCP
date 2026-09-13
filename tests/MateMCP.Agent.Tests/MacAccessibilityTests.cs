using System.Reflection;

namespace MateMCP.Agent.Tests;

public sealed class MacAccessibilityTests
{
    private static string Role(string? role, string? subrole = null)
    {
        var type = typeof(MateMCP.Agent.Desktop.SemanticUiService).Assembly.GetType("MateMCP.Agent.Desktop.MacAccessibility", throwOnError: true)!;
        var method = type.GetMethod("Role", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        return (string)method.Invoke(null, [role, subrole])!;
    }

    [Theory]
    [InlineData("AXButton", null, "button")]
    [InlineData("AXCheckBox", null, "checkbox")]
    [InlineData("AXPopUpButton", null, "combobox")]
    [InlineData("AXTextField", null, "textbox")]
    [InlineData("AXOutline", null, "tree")]
    [InlineData("AXRow", null, "row")]
    [InlineData("AXScrollBar", null, "scrollbar")]
    [InlineData("AXTextField", "AXSecureTextField", "password")]
    [InlineData("AXSecureTextField", null, "password")]
    public void Native_ax_roles_map_to_stable_semantic_roles(string role, string? subrole, string expected)
        => Assert.Equal(expected, Role(role, subrole));
    [Theory]
    [InlineData("ax:0/9/0/41", "ax:0/9/0")]
    [InlineData("ax:0/9", "ax:0")]
    [InlineData("ax:0", null)]
    public void Parent_accessibility_path_is_stable(string id, string? expected)
    {
        var type = typeof(MateMCP.Agent.Desktop.SemanticUiService).Assembly.GetType("MateMCP.Agent.Desktop.MacAccessibility", throwOnError: true)!;
        var method = type.GetMethod("ParentElementId", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        Assert.Equal(expected, method.Invoke(null, [id]));
    }

}
