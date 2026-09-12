using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MateMCP.Agent.Desktop;

[SupportedOSPlatform("macos")]
internal static class MacAccessibility
{
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8 = 0x08000100;

    private const string AxWindows = "AXWindows";
    private const string AxChildren = "AXChildren";
    private const string AxRole = "AXRole";
    private const string AxSubrole = "AXSubrole";
    private const string AxTitle = "AXTitle";
    private const string AxDescription = "AXDescription";
    private const string AxHelp = "AXHelp";
    private const string AxIdentifier = "AXIdentifier";
    private const string AxValue = "AXValue";
    private const string AxEnabled = "AXEnabled";
    private const string AxFocused = "AXFocused";
    private const string AxSelected = "AXSelected";
    private const string AxExpanded = "AXExpanded";
    private const string AxPosition = "AXPosition";
    private const string AxSize = "AXSize";
    private const string AxPress = "AXPress";
    private const string AxScrollToVisible = "AXScrollToVisible";

    private const int AxSuccess = 0;
    private const int AxErrorAttributeUnsupported = -25205;
    private const int AxErrorActionUnsupported = -25206;
    private const int AxErrorNoValue = -25212;
    private const int AxValueCgPoint = 1;
    private const int AxValueCgSize = 2;
    private const int CfNumberDouble = 13;

    public static void EnsureTrusted()
    {
        if (AXIsProcessTrusted()) return;
        var path = Environment.ProcessPath ?? "MateMCP.Agent";
        throw new InvalidOperationException(
            $"macOS Accessibility permission is required for MateMCP Agent. Enable '{path}' in System Settings → Privacy & Security → Accessibility, then restart the Agent. No input or semantic action was performed.");
    }

    public static UiSnapshot Snapshot(DesktopWindowInfo window, int maxElements)
    {
        EnsureTrusted();
        var root = CopyWindow(window.ProcessId, window.Title);
        try
        {
            var elements = new List<UiElementInfo>(Math.Min(maxElements, 256));
            var truncated = false;
            Walk(root, null, "0", 0, maxElements, elements, ref truncated);
            return new UiSnapshot(window.Id, "macos-axui-element", truncated, elements);
        }
        finally { CFRelease(root); }
    }

    public static UiElementInfo Act(
        DesktopWindowInfo window,
        UiElementInfo selected,
        string action,
        string? text,
        bool? expanded)
    {
        EnsureTrusted();
        var root = CopyWindow(window.ProcessId, window.Title);
        IntPtr element = IntPtr.Zero;
        try
        {
            element = ResolvePath(root, selected.Id);
            switch (action)
            {
                case "focus":
                    SetBoolean(element, AxFocused, true, selected, "focus");
                    break;
                case "value":
                    if (selected.Protected)
                        throw new InvalidOperationException("MateMCP refuses to read or replace protected/secure-text values through semantic UI automation.");
                    SetString(element, AxValue, text ?? string.Empty, selected, "set-value");
                    break;
                case "invoke":
                case "toggle":
                    Perform(element, AxPress, selected, action);
                    break;
                case "select":
                    if (!TryPerform(element, AxPress, out var pressError))
                    {
                        if (!IsAttributeSettable(element, AxSelected))
                            throw Unsupported(selected, "select", $"The selected control exposes neither AXPress ({ErrorText(pressError)}) nor a writable AXSelected attribute.");
                        SetBoolean(element, AxSelected, true, selected, "select");
                    }
                    break;
                case "expand":
                case "collapse":
                    if (expanded is null) throw new ArgumentException("expanded is required for expand/collapse.", nameof(expanded));
                    SetBoolean(element, AxExpanded, expanded.Value, selected, action);
                    break;
                case "scroll":
                    Perform(element, AxScrollToVisible, selected, "scroll-into-view");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown macOS semantic action.");
            }

            return BuildInfo(element, selected.Id, selected.ParentId);
        }
        finally
        {
            if (element != IntPtr.Zero) CFRelease(element);
            CFRelease(root);
        }
    }

    private static void Walk(
        IntPtr element,
        string? parentId,
        string path,
        int depth,
        int maxElements,
        List<UiElementInfo> elements,
        ref bool truncated)
    {
        if (elements.Count >= maxElements) { truncated = true; return; }
        var id = "ax:" + path;
        elements.Add(BuildInfo(element, id, parentId));
        if (depth >= 12) return;

        if (!TryCopyAttribute(element, AxChildren, out var children)) return;
        try
        {
            if (CFGetTypeID(children) != CFArrayGetTypeID()) return;
            var count = CFArrayGetCount(children);
            for (nint index = 0; index < count; index++)
            {
                if (elements.Count >= maxElements) { truncated = true; break; }
                var child = CFArrayGetValueAtIndex(children, index);
                if (child == IntPtr.Zero || CFGetTypeID(child) != AXUIElementGetTypeID()) continue;
                Walk(child, id, $"{path}/{index}", depth + 1, maxElements, elements, ref truncated);
            }
        }
        finally { CFRelease(children); }
    }

    private static UiElementInfo BuildInfo(IntPtr element, string id, string? parentId)
    {
        var axRole = StringAttribute(element, AxRole) ?? "AXUnknown";
        var subrole = StringAttribute(element, AxSubrole);
        var protectedValue = IsSecure(axRole, subrole);
        var role = Role(axRole, subrole);
        var name = FirstNonEmpty(
            StringAttribute(element, AxTitle),
            StringAttribute(element, AxDescription),
            StringAttribute(element, AxHelp));
        var automationId = StringAttribute(element, AxIdentifier);
        var value = protectedValue ? null : SimpleAttribute(element, AxValue);
        var enabled = BoolAttribute(element, AxEnabled) ?? true;
        var focused = BoolAttribute(element, AxFocused) ?? false;
        var selected = BoolAttribute(element, AxSelected);
        var expanded = BoolAttribute(element, AxExpanded);
        bool? check = role is "checkbox" or "radiobutton" ? BoolLikeAttribute(element, AxValue) : null;
        var bounds = Bounds(element);
        var actions = ActionNames(element);

        return new UiElementInfo(
            id, parentId, role, name, automationId,
            UiSelectorResolver.SafeValue(value, protectedValue), protectedValue,
            enabled, focused, selected, check, expanded, bounds, actions);
    }

    private static IntPtr CopyWindow(int processId, string title)
    {
        var app = AXUIElementCreateApplication(processId);
        if (app == IntPtr.Zero) throw new InvalidOperationException("macOS Accessibility could not create an application element for the target process.");
        try
        {
            var error = CopyAttribute(app, AxWindows, out var windows);
            if (error != AxSuccess || windows == IntPtr.Zero)
                throw new InvalidOperationException($"macOS Accessibility could not read target windows ({ErrorText(error)}). Verify Accessibility permission and that the target app exposes an accessibility window.");
            try
            {
                if (CFGetTypeID(windows) != CFArrayGetTypeID())
                    throw new InvalidOperationException("macOS Accessibility returned an invalid AXWindows value.");
                var count = CFArrayGetCount(windows);
                if (count <= 0) throw new InvalidOperationException("Target process has no accessible windows.");

                IntPtr fallback = IntPtr.Zero;
                for (nint i = 0; i < count; i++)
                {
                    var candidate = CFArrayGetValueAtIndex(windows, i);
                    if (candidate == IntPtr.Zero || CFGetTypeID(candidate) != AXUIElementGetTypeID()) continue;
                    if (fallback == IntPtr.Zero) fallback = candidate;
                    var candidateTitle = StringAttribute(candidate, AxTitle);
                    if (!string.Equals(candidateTitle, title, StringComparison.Ordinal)) continue;
                    return CFRetain(candidate);
                }
                if (fallback != IntPtr.Zero) return CFRetain(fallback);
                throw new InvalidOperationException("Target process has no usable accessibility windows.");
            }
            finally { CFRelease(windows); }
        }
        finally { CFRelease(app); }
    }

    private static IntPtr ResolvePath(IntPtr root, string elementId)
    {
        if (!elementId.StartsWith("ax:0", StringComparison.Ordinal))
            throw new InvalidOperationException($"Element '{elementId}' is not a macOS Accessibility path.");

        var path = elementId[4..];
        var indexes = string.IsNullOrEmpty(path)
            ? Array.Empty<int>()
            : path.TrimStart('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => int.TryParse(part, out var value) && value >= 0
                    ? value
                    : throw new InvalidOperationException($"Element '{elementId}' contains an invalid Accessibility path."))
                .ToArray();

        var current = CFRetain(root);
        try
        {
            foreach (var index in indexes)
            {
                if (!TryCopyAttribute(current, AxChildren, out var children) || CFGetTypeID(children) != CFArrayGetTypeID())
                    throw new InvalidOperationException("The selected Accessibility element is no longer available. Take a new ui_snapshot and retry.");
                try
                {
                    var count = CFArrayGetCount(children);
                    if (index >= count)
                        throw new InvalidOperationException("The selected Accessibility element is no longer available. Take a new ui_snapshot and retry.");
                    var child = CFArrayGetValueAtIndex(children, index);
                    if (child == IntPtr.Zero || CFGetTypeID(child) != AXUIElementGetTypeID())
                        throw new InvalidOperationException("The selected Accessibility path no longer resolves to a UI element. Take a new ui_snapshot and retry.");
                    var next = CFRetain(child);
                    CFRelease(current);
                    current = next;
                }
                finally { CFRelease(children); }
            }
            var result = current;
            current = IntPtr.Zero;
            return result;
        }
        finally { if (current != IntPtr.Zero) CFRelease(current); }
    }

    private static IReadOnlyList<string> ActionNames(IntPtr element)
    {
        var error = AXUIElementCopyActionNames(element, out var array);
        if (error != AxSuccess || array == IntPtr.Zero) return [];
        try
        {
            if (CFGetTypeID(array) != CFArrayGetTypeID()) return [];
            var count = CFArrayGetCount(array);
            var result = new List<string>((int)Math.Min(count, 32));
            for (nint i = 0; i < count && result.Count < 32; i++)
            {
                var value = CFArrayGetValueAtIndex(array, i);
                var text = StringFromCf(value);
                if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
            }
            return result;
        }
        finally { CFRelease(array); }
    }

    private static UiRect? Bounds(IntPtr element)
    {
        if (!TryCopyAttribute(element, AxPosition, out var position)) return null;
        try
        {
            if (!TryCopyAttribute(element, AxSize, out var size)) return null;
            try
            {
                if (CFGetTypeID(position) != AXValueGetTypeID() || CFGetTypeID(size) != AXValueGetTypeID()) return null;
                if (!AXValueGetPoint(position, AxValueCgPoint, out var point)) return null;
                if (!AXValueGetSize(size, AxValueCgSize, out var dimensions)) return null;
                return new UiRect(point.X, point.Y, dimensions.Width, dimensions.Height);
            }
            finally { CFRelease(size); }
        }
        finally { CFRelease(position); }
    }

    private static string? StringAttribute(IntPtr element, string attribute)
    {
        if (!TryCopyAttribute(element, attribute, out var value)) return null;
        try { return StringFromCf(value); }
        finally { CFRelease(value); }
    }

    private static string? SimpleAttribute(IntPtr element, string attribute)
    {
        if (!TryCopyAttribute(element, attribute, out var value)) return null;
        try
        {
            var type = CFGetTypeID(value);
            if (type == CFStringGetTypeID()) return StringFromCf(value);
            if (type == CFBooleanGetTypeID()) return CFBooleanGetValue(value) ? "true" : "false";
            if (type == CFNumberGetTypeID() && CFNumberGetValue(value, CfNumberDouble, out var number))
                return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return null;
        }
        finally { CFRelease(value); }
    }

    private static bool? BoolAttribute(IntPtr element, string attribute)
    {
        if (!TryCopyAttribute(element, attribute, out var value)) return null;
        try
        {
            var type = CFGetTypeID(value);
            if (type == CFBooleanGetTypeID()) return CFBooleanGetValue(value);
            if (type == CFNumberGetTypeID() && CFNumberGetValue(value, CfNumberDouble, out var number)) return Math.Abs(number) > double.Epsilon;
            return null;
        }
        finally { CFRelease(value); }
    }

    private static bool? BoolLikeAttribute(IntPtr element, string attribute) => BoolAttribute(element, attribute);

    private static bool IsAttributeSettable(IntPtr element, string attribute)
    {
        using var name = CfString(attribute);
        var error = AXUIElementIsAttributeSettable(element, name.Handle, out var settable);
        return error == AxSuccess && settable;
    }

    private static void SetBoolean(IntPtr element, string attribute, bool value, UiElementInfo selected, string action)
    {
        if (!IsAttributeSettable(element, attribute))
            throw Unsupported(selected, action, $"The selected control does not expose a writable {attribute} attribute.");
        using var name = CfString(attribute);
        var cfValue = CfBoolean(value);
        var error = AXUIElementSetAttributeValue(element, name.Handle, cfValue);
        if (error != AxSuccess) throw Unsupported(selected, action, $"Setting {attribute} failed ({ErrorText(error)}).");
    }

    private static void SetString(IntPtr element, string attribute, string value, UiElementInfo selected, string action)
    {
        if (!IsAttributeSettable(element, attribute))
            throw Unsupported(selected, action, $"The selected control does not expose a writable {attribute} attribute.");
        using var name = CfString(attribute);
        using var text = CfString(value);
        var error = AXUIElementSetAttributeValue(element, name.Handle, text.Handle);
        if (error != AxSuccess) throw Unsupported(selected, action, $"Setting {attribute} failed ({ErrorText(error)}).");
    }

    private static void Perform(IntPtr element, string actionName, UiElementInfo selected, string action)
    {
        if (TryPerform(element, actionName, out var error)) return;
        throw Unsupported(selected, action, $"The selected control does not expose or could not perform native {actionName} ({ErrorText(error)}).");
    }

    private static bool TryPerform(IntPtr element, string actionName, out int error)
    {
        using var name = CfString(actionName);
        error = AXUIElementPerformAction(element, name.Handle);
        return error == AxSuccess;
    }

    private static InvalidOperationException Unsupported(UiElementInfo selected, string action, string detail)
    {
        var bounds = selected.Bounds is null
            ? "No coordinate fallback bounds are available."
            : $"Explicit coordinate fallback bounds: x={selected.Bounds.X}, y={selected.Bounds.Y}, width={selected.Bounds.Width}, height={selected.Bounds.Height}.";
        return new InvalidOperationException(
            $"macOS Accessibility could not perform '{action}' on {selected.Role} '{selected.Name ?? selected.AutomationId ?? selected.Id}': {detail} {bounds} Use raw input only as an explicit fallback.");
    }

    private static bool TryCopyAttribute(IntPtr element, string attribute, out IntPtr value)
    {
        var error = CopyAttribute(element, attribute, out value);
        return error == AxSuccess && value != IntPtr.Zero;
    }

    private static int CopyAttribute(IntPtr element, string attribute, out IntPtr value)
    {
        using var name = CfString(attribute);
        return AXUIElementCopyAttributeValue(element, name.Handle, out value);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    internal static string Role(string? role, string? subrole = null)
    {
        if (IsSecure(role, subrole)) return "password";
        return role switch
        {
            "AXButton" or "AXDisclosureTriangle" => "button",
            "AXCheckBox" => "checkbox",
            "AXComboBox" or "AXPopUpButton" => "combobox",
            "AXTextField" or "AXTextArea" => "textbox",
            "AXLink" => "link",
            "AXMenu" => "menu",
            "AXMenuBar" => "menubar",
            "AXMenuItem" => "menuitem",
            "AXRadioButton" => "radiobutton",
            "AXTabGroup" => "tab",
            "AXStaticText" => "text",
            "AXToolbar" => "toolbar",
            "AXOutline" => "tree",
            "AXRow" => "row",
            "AXGroup" => "group",
            "AXTable" => "table",
            "AXWindow" or "AXSheet" => "window",
            "AXScrollArea" => "scrollarea",
            null or "" => "unknown",
            _ => role.StartsWith("AX", StringComparison.Ordinal) ? role[2..].ToLowerInvariant() : role.ToLowerInvariant()
        };
    }

    private static bool IsSecure(string? role, string? subrole)
        => string.Equals(role, "AXSecureTextField", StringComparison.Ordinal)
           || string.Equals(subrole, "AXSecureTextField", StringComparison.Ordinal);

    private static string ErrorText(int error) => error switch
    {
        AxSuccess => "success",
        -25200 => "AX failure",
        -25201 => "illegal argument",
        -25202 => "invalid UI element",
        -25204 => "cannot complete",
        AxErrorAttributeUnsupported => "attribute unsupported",
        AxErrorActionUnsupported => "action unsupported",
        -25208 => "not implemented",
        -25211 => "Accessibility API disabled",
        AxErrorNoValue => "no value",
        _ => $"AX error {error}"
    };

    private static string? StringFromCf(IntPtr value)
    {
        if (value == IntPtr.Zero || CFGetTypeID(value) != CFStringGetTypeID()) return null;
        var length = CFStringGetLength(value);
        var capacity = CFStringGetMaximumSizeForEncoding(length, Utf8) + 1;
        if (capacity <= 1) return string.Empty;
        var buffer = Marshal.AllocHGlobal((int)capacity);
        try
        {
            if (!CFStringGetCString(value, buffer, capacity, Utf8)) return null;
            return Marshal.PtrToStringUTF8(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static CfStringHandle CfString(string value) => new(value);

    private sealed class CfStringHandle : IDisposable
    {
        public IntPtr Handle { get; }
        public CfStringHandle(string value)
        {
            Handle = CFStringCreateWithCString(IntPtr.Zero, value, Utf8);
            if (Handle == IntPtr.Zero) throw new InvalidOperationException("Could not create a CoreFoundation string.");
        }
        public void Dispose() => CFRelease(Handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGPoint(double x, double y) { public readonly double X = x; public readonly double Y = y; }
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGSize(double width, double height) { public readonly double Width = width; public readonly double Height = height; }

    [DllImport(ApplicationServices)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrusted();
    [DllImport(ApplicationServices)] private static extern IntPtr AXUIElementCreateApplication(int pid);
    [DllImport(ApplicationServices)] private static extern nuint AXUIElementGetTypeID();
    [DllImport(ApplicationServices)] private static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);
    [DllImport(ApplicationServices)] private static extern int AXUIElementIsAttributeSettable(IntPtr element, IntPtr attribute, [MarshalAs(UnmanagedType.I1)] out bool settable);
    [DllImport(ApplicationServices)] private static extern int AXUIElementSetAttributeValue(IntPtr element, IntPtr attribute, IntPtr value);
    [DllImport(ApplicationServices)] private static extern int AXUIElementCopyActionNames(IntPtr element, out IntPtr names);
    [DllImport(ApplicationServices)] private static extern int AXUIElementPerformAction(IntPtr element, IntPtr action);
    [DllImport(ApplicationServices, EntryPoint = "AXValueGetValue")]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXValueGetPoint(IntPtr value, int type, out CGPoint point);
    [DllImport(ApplicationServices, EntryPoint = "AXValueGetValue")]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXValueGetSize(IntPtr value, int type, out CGSize size);
    [DllImport(ApplicationServices)] private static extern nuint AXValueGetTypeID();

    [DllImport(CoreFoundation)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CoreFoundation)] private static extern nint CFStringGetLength(IntPtr text);
    [DllImport(CoreFoundation)] private static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr text, IntPtr buffer, nint bufferSize, uint encoding);
    [DllImport(CoreFoundation)] private static extern nuint CFStringGetTypeID();
    [DllImport(CoreFoundation)] private static extern nuint CFBooleanGetTypeID();
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFBooleanGetValue(IntPtr boolean);
    [DllImport(CoreFoundation)] private static extern nuint CFNumberGetTypeID();
    [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(IntPtr number, int type, out double value);
    [DllImport(CoreFoundation)] private static extern nuint CFArrayGetTypeID();
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CoreFoundation)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CoreFoundation)] private static extern nuint CFGetTypeID(IntPtr cf);
    [DllImport(CoreFoundation)] private static extern IntPtr CFRetain(IntPtr cf);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);
    private static IntPtr CfBoolean(bool value)
    {
        var library = NativeLibrary.Load(CoreFoundation);
        try
        {
            var symbol = NativeLibrary.GetExport(library, value ? "kCFBooleanTrue" : "kCFBooleanFalse");
            return Marshal.ReadIntPtr(symbol);
        }
        finally { NativeLibrary.Free(library); }
    }
}
