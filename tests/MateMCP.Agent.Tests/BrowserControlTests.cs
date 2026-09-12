using MateMCP.Agent.Browser;
using MateMCP.Agent.Tools;

namespace MateMCP.Agent.Tests;

public sealed class BrowserControlTests
{
    [Theory]
    [InlineData("visible", "visible")]
    [InlineData(" VISIBLE ", "visible")]
    [InlineData("present", "present")]
    [InlineData("hidden", "hidden")]
    [InlineData("absent", "absent")]
    public void Wait_states_are_normalized(string input, string expected)
        => Assert.Equal(expected, BrowserControlTools.NormalizeWaitState(input));

    [Fact]
    public void Invalid_wait_state_is_rejected()
        => Assert.Throws<ArgumentException>(() => BrowserControlTools.NormalizeWaitState("attached"));

    [Theory]
    [InlineData("combobox", "combobox")]
    [InlineData(" COMBOBOX ", "combobox")]
    [InlineData("listbox", "listbox")]
    public void Select_roles_are_normalized(string input, string expected)
        => Assert.Equal(expected, BrowserControlTools.NormalizeSelectRole(input));

    [Fact]
    public void Select_refuses_non_select_roles()
        => Assert.Throws<ArgumentException>(() => BrowserControlTools.NormalizeSelectRole("textbox"));

    [Fact]
    public void Semantic_matching_is_case_insensitive_and_exact()
    {
        var elements = new[]
        {
            Element("combobox", "Country", "Country", "country-select"),
            Element("combobox", "City", "City", "city-select")
        };

        var matched = BrowserControlTools.MatchElements(
            elements,
            new BrowserSelector(Role: "COMBOBOX", Name: "country"));

        var item = Assert.Single(matched);
        Assert.Equal("Country", item.Name);
    }

    [Fact]
    public void Wait_and_select_tools_are_published_in_MCP_catalog()
    {
        Assert.Contains("browser_wait_for", McpToolCatalog.Names);
        Assert.Contains("browser_select", McpToolCatalog.Names);
    }

    private static BrowserElementInfo Element(string role, string name, string label, string testId)
        => new(
            Id: $"dom:{testId}",
            Tag: role == "combobox" ? "select" : "div",
            Role: role,
            Name: name,
            Text: null,
            Label: label,
            TestId: testId,
            Value: null,
            Protected: false,
            Enabled: true,
            Visible: true,
            Bounds: new BrowserBounds(0, 0, 100, 30),
            Styles: null);
}
