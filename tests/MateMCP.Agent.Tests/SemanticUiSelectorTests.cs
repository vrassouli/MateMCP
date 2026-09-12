using MateMCP.Agent.Desktop;

namespace MateMCP.Agent.Tests;

public sealed class SemanticUiSelectorTests
{
    private static UiElementInfo Element(string id, string role, string? name, string? automationId = null, string? parentId = null, string? value = null, bool protectedValue = false)
        => new(id, parentId, role, name, automationId, UiSelectorResolver.SafeValue(value, protectedValue), protectedValue,
            Enabled: true, Focused: false, Selected: null, Checked: null, Expanded: null,
            Bounds: new UiRect(10, 20, 100, 30), Actions: ["focus", "invoke"]);

    [Fact]
    public void Exact_role_and_name_resolve_one_element()
    {
        var elements = new[]
        {
            Element("1", "button", "Management"),
            Element("2", "button", "Devices")
        };

        var result = UiSelectorResolver.Resolve(elements, new UiSelector(Role: "BUTTON", Name: " management "));

        Assert.Equal("1", result.Id);
    }

    [Fact]
    public void Ambiguous_selector_never_picks_an_arbitrary_element()
    {
        var elements = new[]
        {
            Element("1", "button", "Open", parentId: "dialog-a"),
            Element("2", "button", "Open", parentId: "dialog-b")
        };

        Assert.Throws<UiSelectorAmbiguousException>(() =>
            UiSelectorResolver.Resolve(elements, new UiSelector(Role: "button", Name: "Open")));
    }

    [Fact]
    public void Parent_id_can_disambiguate_repeated_controls()
    {
        var elements = new[]
        {
            Element("1", "button", "Open", parentId: "dialog-a"),
            Element("2", "button", "Open", parentId: "dialog-b")
        };

        var result = UiSelectorResolver.Resolve(elements, new UiSelector(Role: "button", Name: "Open", ParentId: "dialog-b"));

        Assert.Equal("2", result.Id);
    }

    [Fact]
    public void Index_is_explicit_and_bounds_checked()
    {
        var elements = new[]
        {
            Element("1", "row", "Result"),
            Element("2", "row", "Result")
        };

        Assert.Equal("2", UiSelectorResolver.Resolve(elements, new UiSelector(Role: "row", Name: "Result", Index: 1)).Id);
        Assert.Throws<UiSelectorNotFoundException>(() =>
            UiSelectorResolver.Resolve(elements, new UiSelector(Role: "row", Name: "Result", Index: 2)));
    }

    [Fact]
    public void Protected_values_are_never_returned()
    {
        var password = Element("pwd", "password", "Password", value: "super-secret", protectedValue: true);

        Assert.True(password.Protected);
        Assert.Null(password.Value);
    }

    [Fact]
    public void Automation_id_can_disambiguate_controls_with_same_name()
    {
        var elements = new[]
        {
            Element("1", "textbox", "Search", automationId: "global-search"),
            Element("2", "textbox", "Search", automationId: "device-search")
        };

        var result = UiSelectorResolver.Resolve(elements,
            new UiSelector(Role: "textbox", Name: "Search", AutomationId: "device-search"));

        Assert.Equal("2", result.Id);
    }
}
