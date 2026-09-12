using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MateMCP.Agent.Desktop;

public sealed class SemanticUiService
{
    private readonly DesktopVisionService _vision = new();

    public async Task<UiSnapshot> SnapshotAsync(string windowId, int maxElements = 400, CancellationToken cancellationToken = default)
    {
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        maxElements = Math.Clamp(maxElements, 1, 1000);
        if (OperatingSystem.IsWindows()) return WindowsSemanticUi.Snapshot(window, maxElements);
        if (OperatingSystem.IsMacOS()) return await MacSemanticUi.SnapshotAsync(window, maxElements, cancellationToken);
        throw UnsupportedPlatform();
    }

    public async Task<UiElementInfo> FocusAsync(string windowId, UiSelector selector, CancellationToken cancellationToken = default)
    {
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        if (OperatingSystem.IsWindows()) return WindowsSemanticUi.Act(window, selector, "focus", null);
        if (OperatingSystem.IsMacOS()) return MacSemanticUi.Act(window, selector, "focus", null);
        throw UnsupportedPlatform();
    }

    public async Task<UiElementInfo> ClickAsync(string windowId, UiSelector selector, CancellationToken cancellationToken = default)
    {
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        if (OperatingSystem.IsWindows()) return WindowsSemanticUi.Act(window, selector, "invoke", null);
        if (OperatingSystem.IsMacOS()) return MacSemanticUi.Act(window, selector, "invoke", null);
        throw UnsupportedPlatform();
    }

    public async Task<UiElementInfo> TypeAsync(string windowId, UiSelector selector, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 20_000) throw new ArgumentOutOfRangeException(nameof(text), "Text entry is limited to 20,000 characters per call.");
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        if (OperatingSystem.IsWindows()) return WindowsSemanticUi.Act(window, selector, "value", text);
        if (OperatingSystem.IsMacOS()) return MacSemanticUi.Act(window, selector, "value", text);
        throw UnsupportedPlatform();
    }

    public async Task<UiElementInfo> ToggleAsync(string windowId, UiSelector selector, CancellationToken cancellationToken = default)
    {
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        if (OperatingSystem.IsWindows()) return WindowsSemanticUi.Act(window, selector, "toggle", null);
        if (OperatingSystem.IsMacOS()) return MacSemanticUi.Act(window, selector, "toggle", null);
        throw UnsupportedPlatform();
    }

    public async Task<UiElementInfo> SelectAsync(string windowId, UiSelector selector, CancellationToken cancellationToken = default)
    {
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        if (OperatingSystem.IsWindows()) return WindowsSemanticUi.Act(window, selector, "select", null);
        if (OperatingSystem.IsMacOS()) return MacSemanticUi.Act(window, selector, "select", null);
        throw UnsupportedPlatform();
    }

    public async Task<UiElementInfo> SetExpandedAsync(string windowId, UiSelector selector, bool expanded, CancellationToken cancellationToken = default)
    {
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        if (OperatingSystem.IsWindows()) return WindowsSemanticUi.Act(window, selector, expanded ? "expand" : "collapse", null);
        if (OperatingSystem.IsMacOS()) return MacSemanticUi.Act(window, selector, expanded ? "expand" : "collapse", null, expanded);
        throw UnsupportedPlatform();
    }

    public async Task<UiElementInfo> ScrollIntoViewAsync(string windowId, UiSelector selector, CancellationToken cancellationToken = default)
    {
        var window = await ResolveWindowAsync(windowId, cancellationToken);
        if (OperatingSystem.IsWindows()) return WindowsSemanticUi.Act(window, selector, "scroll", null);
        if (OperatingSystem.IsMacOS()) return MacSemanticUi.Act(window, selector, "scroll", null);
        throw UnsupportedPlatform();
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

    [SupportedOSPlatform("windows")]
    private static class WindowsSemanticUi
    {
        private static readonly Guid CUiAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
        private const int TreeScopeDescendants = 4;

        private const int BoundingRectangleProperty = 30001;
        private const int ControlTypeProperty = 30003;
        private const int NameProperty = 30005;
        private const int HasKeyboardFocusProperty = 30008;
        private const int IsEnabledProperty = 30010;
        private const int AutomationIdProperty = 30011;
        private const int IsPasswordProperty = 30019;
        private const int ValueValueProperty = 30045;
        private const int ExpandCollapseStateProperty = 30070;
        private const int SelectionItemIsSelectedProperty = 30079;
        private const int ToggleStateProperty = 30086;

        private const int InvokePattern = 10000;
        private const int ValuePattern = 10002;
        private const int ExpandCollapsePattern = 10005;
        private const int SelectionItemPattern = 10010;
        private const int TogglePattern = 10015;
        private const int ScrollItemPattern = 10017;

        public static UiSnapshot Snapshot(DesktopWindowInfo window, int maxElements)
        {
            var traversal = Traverse(window, maxElements);
            return new UiSnapshot(window.Id, "windows-uia", traversal.Truncated, traversal.Elements);
        }

        public static UiElementInfo Act(DesktopWindowInfo window, UiSelector selector, string action, string? text)
        {
            var traversal = Traverse(window, 1000);
            var selected = UiSelectorResolver.Resolve(traversal.Elements, selector);
            if (!traversal.Handles.TryGetValue(selected.Id, out var handle))
                throw new InvalidOperationException("The selected UI element became unavailable. Take a new ui_snapshot and retry.");

            dynamic element = handle;
            try
            {
                switch (action)
                {
                    case "focus":
                        element.SetFocus();
                        break;
                    case "invoke":
                    {
                        dynamic pattern = GetPattern(element, InvokePattern, "invoke", selected);
                        pattern.Invoke();
                        break;
                    }
                    case "value":
                    {
                        if (selected.Protected)
                            throw new InvalidOperationException("MateMCP refuses to read or replace protected/secure-text values through semantic UI automation.");
                        dynamic pattern = GetPattern(element, ValuePattern, "set-value", selected);
                        pattern.SetValue(text ?? string.Empty);
                        break;
                    }
                    case "toggle":
                    {
                        dynamic pattern = GetPattern(element, TogglePattern, "toggle", selected);
                        pattern.Toggle();
                        break;
                    }
                    case "select":
                    {
                        dynamic pattern = GetPattern(element, SelectionItemPattern, "select", selected);
                        pattern.Select();
                        break;
                    }
                    case "expand":
                    {
                        dynamic pattern = GetPattern(element, ExpandCollapsePattern, "expand", selected);
                        pattern.Expand();
                        break;
                    }
                    case "collapse":
                    {
                        dynamic pattern = GetPattern(element, ExpandCollapsePattern, "collapse", selected);
                        pattern.Collapse();
                        break;
                    }
                    case "scroll":
                    {
                        dynamic pattern = GetPattern(element, ScrollItemPattern, "scroll-into-view", selected);
                        pattern.ScrollIntoView();
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(action));
                }

                return BuildInfo(element, selected.Id, selected.ParentId);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException($"Windows UI Automation could not perform '{action}' on {selected.Role} '{selected.Name ?? selected.AutomationId ?? selected.Id}': {ex.Message}", ex);
            }
        }

        private static Traversal Traverse(DesktopWindowInfo window, int maxElements)
        {
            if (!long.TryParse(window.Id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
                throw new InvalidOperationException($"Window '{window.Id}' is not a valid Windows HWND identifier.");

            var automationType = Type.GetTypeFromCLSID(CUiAutomation, throwOnError: true)
                ?? throw new InvalidOperationException("Windows UI Automation is unavailable.");
            dynamic automation = Activator.CreateInstance(automationType)
                ?? throw new InvalidOperationException("Windows UI Automation could not be initialized.");
            dynamic root = automation.ElementFromHandle(new IntPtr(raw));
            dynamic walker = automation.ControlViewWalker;

            var elements = new List<UiElementInfo>(Math.Min(maxElements, 256));
            var handles = new Dictionary<string, object>(StringComparer.Ordinal);
            var rootId = ElementId(root, "uia:root");
            Add(root, rootId, null);

            dynamic collection = root.FindAll(TreeScopeDescendants, automation.TrueCondition);
            var length = Convert.ToInt32(collection.Length, CultureInfo.InvariantCulture);
            var truncated = length + 1 > maxElements;
            var count = Math.Min(length, Math.Max(0, maxElements - 1));
            for (var index = 0; index < count; index++)
            {
                dynamic element = collection.GetElement(index);
                var id = ElementId(element, $"uia:{index + 1}");
                string? parentId = null;
                try
                {
                    dynamic parent = walker.GetParentElement(element);
                    if (parent is not null) parentId = ElementId(parent, rootId);
                }
                catch { }
                Add(element, id, parentId);
            }

            return new Traversal(elements, handles, truncated);

            void Add(dynamic element, string id, string? parentId)
            {
                if (handles.ContainsKey(id)) id += ":" + elements.Count.ToString(CultureInfo.InvariantCulture);
                var info = BuildInfo(element, id, parentId);
                elements.Add(info);
                handles[id] = (object)element;
            }
        }

        private static UiElementInfo BuildInfo(dynamic element, string id, string? parentId)
        {
            var isProtected = BoolProperty(element, IsPasswordProperty);
            var role = Role(IntProperty(element, ControlTypeProperty));
            var name = StringProperty(element, NameProperty);
            var automationId = StringProperty(element, AutomationIdProperty);
            var value = isProtected ? null : StringProperty(element, ValueValueProperty);
            var enabled = BoolProperty(element, IsEnabledProperty, defaultValue: true);
            var focused = BoolProperty(element, HasKeyboardFocusProperty);
            var selected = NullableBoolProperty(element, SelectionItemIsSelectedProperty);
            var toggleState = NullableIntProperty(element, ToggleStateProperty);
            var expandedState = NullableIntProperty(element, ExpandCollapseStateProperty);
            var bounds = BoundsProperty(element);
            var actions = AvailableActions(element, enabled, isProtected);

            return new UiElementInfo(
                id,
                parentId,
                role,
                name,
                automationId,
                UiSelectorResolver.SafeValue(value, isProtected),
                isProtected,
                enabled,
                focused,
                selected,
                toggleState is null ? null : toggleState == 1,
                expandedState is null ? (bool?)null : expandedState == 1,
                bounds,
                actions);
        }

        private static IReadOnlyList<string> AvailableActions(dynamic element, bool enabled, bool isProtected)
        {
            var actions = new List<string>();
            if (enabled) actions.Add("focus");
            if (HasPattern(element, InvokePattern)) actions.Add("invoke");
            if (!isProtected && HasPattern(element, ValuePattern)) actions.Add("set-value");
            if (HasPattern(element, TogglePattern)) actions.Add("toggle");
            if (HasPattern(element, SelectionItemPattern)) actions.Add("select");
            if (HasPattern(element, ExpandCollapsePattern)) { actions.Add("expand"); actions.Add("collapse"); }
            if (HasPattern(element, ScrollItemPattern)) actions.Add("scroll-into-view");
            return actions;
        }

        private static dynamic GetPattern(dynamic element, int patternId, string action, UiElementInfo selected)
        {
            try
            {
                dynamic pattern = element.GetCurrentPattern(patternId);
                if (pattern is null) throw new InvalidOperationException();
                return pattern;
            }
            catch
            {
                var bounds = selected.Bounds is null
                    ? "No coordinate fallback bounds are available."
                    : $"Explicit coordinate fallback bounds: x={selected.Bounds.X}, y={selected.Bounds.Y}, width={selected.Bounds.Width}, height={selected.Bounds.Height}.";
                throw new InvalidOperationException($"The selected {selected.Role} does not expose the native '{action}' pattern. {bounds} Use raw input only as an explicit fallback.");
            }
        }

        private static bool HasPattern(dynamic element, int patternId)
        {
            try { return element.GetCurrentPattern(patternId) is not null; }
            catch { return false; }
        }

        private static object? Property(dynamic element, int propertyId)
        {
            try { return element.GetCurrentPropertyValue(propertyId); }
            catch { return null; }
        }

        private static string? StringProperty(dynamic element, int propertyId)
        {
            var value = Property(element, propertyId);
            if (value is null) return null;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static int IntProperty(dynamic element, int propertyId)
        {
            var value = Property(element, propertyId);
            try { return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static int? NullableIntProperty(dynamic element, int propertyId)
        {
            var value = Property(element, propertyId);
            try { return value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static bool BoolProperty(dynamic element, int propertyId, bool defaultValue = false)
        {
            var value = Property(element, propertyId);
            try { return value is null ? defaultValue : Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
            catch { return defaultValue; }
        }

        private static bool? NullableBoolProperty(dynamic element, int propertyId)
        {
            var value = Property(element, propertyId);
            try { return value is null ? null : Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }

        private static UiRect? BoundsProperty(dynamic element)
        {
            var value = Property(element, BoundingRectangleProperty);
            if (value is not Array array || array.Length < 4) return null;
            try
            {
                return new UiRect(
                    Convert.ToDouble(array.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToDouble(array.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToDouble(array.GetValue(2), CultureInfo.InvariantCulture),
                    Convert.ToDouble(array.GetValue(3), CultureInfo.InvariantCulture));
            }
            catch { return null; }
        }

        private static string ElementId(dynamic element, string fallback)
        {
            try
            {
                var runtime = element.GetRuntimeId();
                if (runtime is Array array && array.Length > 0)
                {
                    var parts = new string[array.Length];
                    for (var i = 0; i < array.Length; i++)
                        parts[i] = Convert.ToString(array.GetValue(i), CultureInfo.InvariantCulture) ?? "0";
                    return "uia:" + string.Join('.', parts);
                }
            }
            catch { }
            return fallback;
        }

        private static string Role(int controlType) => controlType switch
        {
            50000 => "button", 50002 => "checkbox", 50003 => "combobox", 50004 => "textbox",
            50005 => "link", 50007 => "listitem", 50008 => "list", 50009 => "menu",
            50010 => "menubar", 50011 => "menuitem", 50013 => "radiobutton", 50018 => "tab",
            50019 => "tabitem", 50020 => "text", 50021 => "toolbar", 50023 => "tree",
            50024 => "treeitem", 50026 => "group", 50028 => "datagrid", 50029 => "dataitem",
            50030 => "document", 50032 => "window", 50033 => "pane", 50036 => "table",
            50037 => "titlebar", 50038 => "separator", _ => controlType == 0 ? "unknown" : $"control-{controlType}"
        };

        private sealed record Traversal(List<UiElementInfo> Elements, Dictionary<string, object> Handles, bool Truncated);
    }

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
