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
            "ui_scroll_into_view"
        };

        foreach (var tool in expected)
            Assert.Contains(tool, McpToolCatalog.Names);
    }
}
