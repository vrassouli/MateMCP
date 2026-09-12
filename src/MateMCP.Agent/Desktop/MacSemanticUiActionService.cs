namespace MateMCP.Agent.Desktop;

public sealed class MacSemanticUiActionService
{
    private readonly DesktopVisionService _vision = new();
    private readonly SemanticUiService _semantic = new();

    public async Task<UiElementInfo> ActAsync(
        string windowId,
        UiSelector selector,
        string action,
        string? text = null,
        bool? expanded = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Native macOS semantic actions are only available on macOS.");

        var before = await _semantic.SnapshotAsync(windowId, 1000, cancellationToken);
        var selected = UiSelectorResolver.Resolve(before.Elements, selector);
        if (action == "value" && selected.Protected)
            throw new InvalidOperationException("MateMCP refuses to read or replace protected/secure-text values through semantic UI automation.");

        var window = await ResolveWindowAsync(windowId, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        MacAccessibility.Act(window, selected, action, text, expanded);

        try
        {
            var after = await _semantic.SnapshotAsync(windowId, 1000, cancellationToken);
            return after.Elements.FirstOrDefault(element => string.Equals(element.Id, selected.Id, StringComparison.Ordinal))
                ?? selected;
        }
        catch (InvalidOperationException)
        {
            return selected;
        }
    }

    private async Task<DesktopWindowInfo> ResolveWindowAsync(string windowId, CancellationToken cancellationToken)
    {
        var windows = await _vision.ListWindowsAsync(cancellationToken);
        return windows.FirstOrDefault(window => string.Equals(window.Id, windowId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Window '{windowId}' is no longer available. Call window_list again.");
    }
}
