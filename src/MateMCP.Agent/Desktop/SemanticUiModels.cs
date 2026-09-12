namespace MateMCP.Agent.Desktop;

public sealed record UiRect(double X, double Y, double Width, double Height);

public sealed record UiElementInfo(
    string Id,
    string? ParentId,
    string Role,
    string? Name,
    string? AutomationId,
    string? Value,
    bool Protected,
    bool Enabled,
    bool Focused,
    bool? Selected,
    bool? Checked,
    bool? Expanded,
    UiRect? Bounds,
    IReadOnlyList<string> Actions);

public sealed record UiSnapshot(
    string WindowId,
    string Platform,
    bool Truncated,
    IReadOnlyList<UiElementInfo> Elements);

public sealed record UiSelector(
    string? Role = null,
    string? Name = null,
    string? AutomationId = null,
    string? ParentId = null,
    int? Index = null);

public sealed class UiSelectorNotFoundException(string message) : InvalidOperationException(message);
public sealed class UiSelectorAmbiguousException(string message) : InvalidOperationException(message);

public static class UiSelectorResolver
{
    public static UiElementInfo Resolve(IReadOnlyList<UiElementInfo> elements, UiSelector selector)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(selector);

        var candidates = elements.Where(element => Matches(element, selector)).ToArray();
        if (candidates.Length == 0)
            throw new UiSelectorNotFoundException($"No UI element matched {Describe(selector)}.");

        if (selector.Index is { } index)
        {
            if (index < 0 || index >= candidates.Length)
                throw new UiSelectorNotFoundException($"Selector {Describe(selector)} matched {candidates.Length} element(s), but index {index} is out of range.");
            return candidates[index];
        }

        if (candidates.Length > 1)
        {
            var examples = string.Join(", ", candidates.Take(5).Select(candidate => $"{candidate.Role} '{candidate.Name ?? candidate.AutomationId ?? candidate.Id}'"));
            throw new UiSelectorAmbiguousException($"Selector {Describe(selector)} matched {candidates.Length} elements ({examples}). Add automationId, parentId, or index to disambiguate.");
        }

        return candidates[0];
    }

    public static bool Matches(UiElementInfo element, UiSelector selector)
    {
        if (!string.IsNullOrWhiteSpace(selector.Role) && !Same(element.Role, selector.Role)) return false;
        if (!string.IsNullOrWhiteSpace(selector.Name) && !Same(element.Name, selector.Name)) return false;
        if (!string.IsNullOrWhiteSpace(selector.AutomationId) && !Same(element.AutomationId, selector.AutomationId)) return false;
        if (!string.IsNullOrWhiteSpace(selector.ParentId) && !Same(element.ParentId, selector.ParentId)) return false;
        return true;
    }

    public static string? SafeValue(string? value, bool isProtected)
        => isProtected ? null : value;

    private static bool Same(string? actual, string? requested)
        => string.Equals(actual?.Trim(), requested?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Describe(UiSelector selector)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(selector.Role)) parts.Add($"role='{selector.Role}'");
        if (!string.IsNullOrWhiteSpace(selector.Name)) parts.Add($"name='{selector.Name}'");
        if (!string.IsNullOrWhiteSpace(selector.AutomationId)) parts.Add($"automationId='{selector.AutomationId}'");
        if (!string.IsNullOrWhiteSpace(selector.ParentId)) parts.Add($"parentId='{selector.ParentId}'");
        if (selector.Index is not null) parts.Add($"index={selector.Index}");
        return parts.Count == 0 ? "the empty selector" : string.Join(", ", parts);
    }
}
