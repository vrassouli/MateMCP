using MateMCP.Agent.Tools;

namespace MateMCP.Agent.Tests;

public sealed class SemanticUiToolTests
{
    [Fact]
    public void Semantic_ui_tools_are_published_in_the_MCP_catalog()
    {
        var expected = new[]
        {
            "ui_snapshot",
            "ui_click",
            "ui_type",
            "ui_focus",
            "ui_toggle",
            "ui_select",
            "ui_expand",
            "ui_click_at",
            "ui_scroll_into_view"
        };

        foreach (var tool in expected)
            Assert.Contains(tool, McpToolCatalog.Names);
    }
    [Fact]
    public void Windows_isolated_click_uses_UIA_point_hit_testing_and_process_guard()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Desktop", "SemanticUiService.cs"));
        var tools = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Tools", "DesktopSemanticTools.cs"));

        Assert.Contains("ElementFromPoint", service, StringComparison.Ordinal);
        Assert.Contains("ProcessIdProperty", service, StringComparison.Ordinal);
        Assert.Contains("resolved to a different process", service, StringComparison.Ordinal);
        Assert.Contains("OperatingSystem.IsWindows()", tools, StringComparison.Ordinal);
        Assert.Contains("_semantic.ClickAtAsync", tools, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MateMCP.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repository root.");
    }

}
