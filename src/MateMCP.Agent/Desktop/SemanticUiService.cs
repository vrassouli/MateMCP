using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MateMCP.Agent.Desktop;

public sealed class SemanticUiService
{
    private readonly DesktopVisionService _vision = new();
    private readonly WindowsSemanticUiHelper _windows = new();

    public async Task<UiSnapshot> SnapshotAsync(string windowId, int maxElements = 400, CancellationToken cancellationToken = default)
    {
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        maxElements = Math.Clamp(maxElements, 1, 1000);
        UiSnapshot snapshot;
        if (OperatingSystem.IsWindows()) snapshot = await _windows.SnapshotAsync(window.Id, maxElements, cancellationToken);
        else if (OperatingSystem.IsMacOS()) snapshot = await MacSemanticUi.SnapshotAsync(window, maxElements, cancellationToken);
        else throw UnsupportedPlatform();
        return SensitiveUiGuard.Shared.RedactAndReconcile(snapshot);
    }

    public async Task<UiElementInfo> FocusAsync(string windowId, UiSelector selector, CancellationToken cancellationToken = default)
    {
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        if (OperatingSystem.IsWindows()) return await _windows.ActAsync(window.Id, selector, "focus", null, cancellationToken);
        if (OperatingSystem.IsMacOS()) return MacSemanticUi.Act(window, selector, "focus", null);
        throw UnsupportedPlatform();
    }

    public async Task<UiElementInfo> ClickAsync(string windowId, UiSelector selector, CancellationToken cancellationToken = default)
    {
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        if (OperatingSystem.IsWindows()) return await _windows.ActAsync(window.Id, selector, "invoke", null, cancellationToken);
        if (OperatingSystem.IsMacOS()) return MacSemanticUi.Act(window, selector, "invoke", null);
        throw UnsupportedPlatform();
    }

    public async Task<UiElementInfo> TypeAsync(string windowId, UiSelector selector, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 20_000) throw new ArgumentOutOfRangeException(nameof(text), "Text entry is limited to 20,000 characters per call.");
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        if (OperatingSystem.IsWindows()) return await _windows.ActAsync(window.Id, selector, "value", text, cancellationToken);
        if (OperatingSystem.IsMacOS()) return MacSemanticUi.Act(window, selector, "value", text);
        throw UnsupportedPlatform();
    }

    public async Task<UiElementInfo> ResolveAsync(string windowId, UiSelector selector, CancellationToken cancellationToken = default)
    {
        var snapshot = await SnapshotAsync(windowId, 1000, cancellationToken);
        return UiSelectorResolver.Resolve(snapshot.Elements, selector);
    }

    public async Task<UiElementInfo> FillSecretAsync(string windowId, UiElementInfo expected, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentException.ThrowIfNullOrWhiteSpace(expected.Id);
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length > 16_384) throw new ArgumentOutOfRangeException(nameof(secret), "Secret value is too large.");
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        if (OperatingSystem.IsWindows()) return await _windows.FillSecretAsync(window.Id, expected, secret, cancellationToken);
        if (OperatingSystem.IsMacOS()) return MacAccessibility.FillSecret(window, expected, secret);
        throw UnsupportedPlatform();
    }

    public async Task<UiElementInfo> ToggleAsync(string windowId, UiSelector selector, CancellationToken cancellationToken = default)
    {
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        if (OperatingSystem.IsWindows()) return await _windows.ActAsync(window.Id, selector, "toggle", null, cancellationToken);
        if (OperatingSystem.IsMacOS()) return MacSemanticUi.Act(window, selector, "toggle", null);
        throw UnsupportedPlatform();
    }

    public async Task<UiElementInfo> SelectAsync(string windowId, UiSelector selector, CancellationToken cancellationToken = default)
    {
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        if (OperatingSystem.IsWindows()) return await _windows.ActAsync(window.Id, selector, "select", null, cancellationToken);
        if (OperatingSystem.IsMacOS()) return MacSemanticUi.Act(window, selector, "select", null);
        throw UnsupportedPlatform();
    }

    public async Task<UiElementInfo> SetExpandedAsync(string windowId, UiSelector selector, bool expanded, CancellationToken cancellationToken = default)
    {
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        if (OperatingSystem.IsWindows()) return await _windows.ActAsync(window.Id, selector, expanded ? "expand" : "collapse", null, cancellationToken);
        if (OperatingSystem.IsMacOS()) return MacSemanticUi.Act(window, selector, expanded ? "expand" : "collapse", null, expanded);
        throw UnsupportedPlatform();
    }

    public async Task<UiElementInfo> ScrollIntoViewAsync(string windowId, UiSelector selector, CancellationToken cancellationToken = default)
    {
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        if (OperatingSystem.IsWindows()) return await _windows.ActAsync(window.Id, selector, "scroll", null, cancellationToken);
        if (OperatingSystem.IsMacOS()) return MacSemanticUi.Act(window, selector, "scroll", null);
        throw UnsupportedPlatform();
    }

    public async Task<UiElementInfo> ClickAtAsync(string windowId, double x, double y, CancellationToken cancellationToken = default)
    {
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        if (OperatingSystem.IsWindows()) return await _windows.ClickAtAsync(window.Id, x, y, cancellationToken);
        throw new PlatformNotSupportedException("Window-relative semantic hit-testing through SemanticUiService is currently supported on Windows.");
    }

    private async Task<DesktopWindowInfo> ResolveWindowAsync(string windowId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(windowId)) throw new ArgumentException("windowId is required.", nameof(windowId));
        var windows = await _vision.ListWindowsAsync(cancellationToken);
        return windows.FirstOrDefault(window => string.Equals(window.Id, windowId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Window '{windowId}' is no longer available. Call window_list again.");
    }

    private static PlatformNotSupportedException UnsupportedPlatform()
        => new("Semantic desktop UI automation currently supports Windows inspection/actions and macOS inspection.");

    [SupportedOSPlatform("macos")]
    private static class MacSemanticUi
    {
        public static Task<UiSnapshot> SnapshotAsync(DesktopWindowInfo window, int maxElements, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(MacAccessibility.Snapshot(window, maxElements));
        }

        public static UiElementInfo Act(DesktopWindowInfo window, UiSelector selector, string action, string? text, bool? expanded = null)
        {
            var snapshot = MacAccessibility.Snapshot(window, 1000);
            var selected = UiSelectorResolver.Resolve(snapshot.Elements, selector);
            return MacAccessibility.Act(window, selected, action, text, expanded);
        }
    }
}
