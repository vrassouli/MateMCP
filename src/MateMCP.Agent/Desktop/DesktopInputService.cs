using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MateMCP.Agent.Desktop;

public sealed class DesktopInputService
{
    public void MoveMouse(int x, int y)
    {
        if (OperatingSystem.IsWindows()) { WindowsInput.MoveMouse(x, y); return; }
        if (OperatingSystem.IsMacOS()) { MacInput.MoveMouse(x, y); return; }
        throw UnsupportedPlatform();
    }

    public void ClickMouse(int x, int y, string button = "left", int clickCount = 1)
    {
        button = NormalizeButton(button);
        clickCount = Math.Clamp(clickCount, 1, 3);
        if (OperatingSystem.IsWindows()) { WindowsInput.ClickMouse(x, y, button, clickCount); return; }
        if (OperatingSystem.IsMacOS()) { MacInput.ClickMouse(x, y, button, clickCount); return; }
        throw UnsupportedPlatform();
    }

    public void DragMouse(int fromX, int fromY, int toX, int toY, string button = "left", int durationMs = 350)
    {
        button = NormalizeButton(button);
        durationMs = Math.Clamp(durationMs, 0, 5000);
        if (OperatingSystem.IsWindows()) { WindowsInput.DragMouse(fromX, fromY, toX, toY, button, durationMs); return; }
        if (OperatingSystem.IsMacOS()) { MacInput.DragMouse(fromX, fromY, toX, toY, button, durationMs); return; }
        throw UnsupportedPlatform();
    }

    public void ScrollMouse(int deltaX, int deltaY, int? x = null, int? y = null)
    {
        if (OperatingSystem.IsWindows()) { WindowsInput.ScrollMouse(deltaX, deltaY, x, y); return; }
        if (OperatingSystem.IsMacOS()) { MacInput.ScrollMouse(deltaX, deltaY, x, y); return; }
        throw UnsupportedPlatform();
    }

    public void TypeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 20_000) throw new ArgumentOutOfRangeException(nameof(text), "Text entry is limited to 20,000 characters per call.");
        if (OperatingSystem.IsWindows()) { WindowsInput.TypeText(text); return; }
        if (OperatingSystem.IsMacOS()) { MacInput.TypeText(text); return; }
        throw UnsupportedPlatform();
    }

    public void PressKey(string key)
    {
        key = NormalizeKey(key);
        if (OperatingSystem.IsWindows()) { WindowsInput.PressKey(key); return; }
        if (OperatingSystem.IsMacOS()) { MacInput.PressKey(key); return; }
        throw UnsupportedPlatform();
    }

    public void PressShortcut(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(keys), "A shortcut must contain between 1 and 8 keys.");
        var normalized = keys.Select(NormalizeKey).ToArray();
        if (OperatingSystem.IsWindows()) { WindowsInput.PressShortcut(normalized); return; }
        if (OperatingSystem.IsMacOS()) { MacInput.PressShortcut(normalized); return; }
        throw UnsupportedPlatform();
    }

    public void FocusWindow(string windowId, int processId)
    {
        if (string.IsNullOrWhiteSpace(windowId)) throw new ArgumentException("Window id is required.", nameof(windowId));
        if (OperatingSystem.IsWindows()) { WindowsInput.FocusWindow(windowId); return; }
        if (OperatingSystem.IsMacOS()) { MacInput.FocusApplication(processId); return; }
        throw UnsupportedPlatform();
    }

    public static string NormalizeButton(string button)
        => (button ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "left" => "left",
            "right" => "right",
            "middle" => "middle",
            _ => throw new ArgumentException("Mouse button must be left, right, or middle.", nameof(button))
        };

    public static string NormalizeKey(string key)
    {
        key = (key ?? string.Empty).Trim().ToUpperInvariant();
        if (key.Length == 1 && char.IsLetterOrDigit(key[0])) return key;
        return key switch
        {
            "RETURN" => "ENTER",
            "ESCAPE" => "ESC",
            "CONTROL" => "CTRL",
            "OPTION" => "ALT",
            "COMMAND" => "CMD",
            "WINDOWS" or "WIN" or "META" => "CMD",
            "PAGEUP" or "PAGE_UP" => "PAGEUP",
            "PAGEDOWN" or "PAGE_DOWN" => "PAGEDOWN",
            "ARROWUP" or "UP" => "UP",
            "ARROWDOWN" or "DOWN" => "DOWN",
            "ARROWLEFT" or "LEFT" => "LEFT",
            "ARROWRIGHT" or "RIGHT" => "RIGHT",
            "ENTER" or "ESC" or "TAB" or "SPACE" or "BACKSPACE" or "DELETE" or "INSERT" or
            "HOME" or "END" or "PAGEUP" or "PAGEDOWN" or "UP" or "DOWN" or "LEFT" or "RIGHT" or
            "SHIFT" or "CTRL" or "ALT" or "CMD" or
            "F1" or "F2" or "F3" or "F4" or "F5" or "F6" or "F7" or "F8" or "F9" or "F10" or "F11" or "F12" => key,
            _ => throw new ArgumentException($"Unsupported key '{key}'.", nameof(key))
        };
    }

    private static PlatformNotSupportedException UnsupportedPlatform()
        => new("Desktop input currently supports Windows and macOS Agents.");

    [SupportedOSPlatform("windows")]
    private static class WindowsInput
    {
        private const uint MouseLeftDown = 0x0002;
        private const uint MouseLeftUp = 0x0004;
        private const uint MouseRightDown = 0x0008;
        private const uint MouseRightUp = 0x0010;
        private const uint MouseMiddleDown = 0x0020;
        private const uint MouseMiddleUp = 0x0040;
        private const uint MouseWheel = 0x0800;
        private const uint MouseHWheel = 0x01000;
        private const uint KeyEventKeyUp = 0x0002;
        private const uint KeyEventUnicode = 0x0004;
        private const int SwRestore = 9;

        public static void MoveMouse(int x, int y)
        {
            if (!SetCursorPos(x, y)) throw new InvalidOperationException("Windows failed to move the mouse pointer.");
        }

        public static void ClickMouse(int x, int y, string button, int clickCount)
        {
            MoveMouse(x, y);
            var (down, up) = MouseFlags(button);
            for (var index = 0; index < clickCount; index++)
            {
                mouse_event(down, 0, 0, 0, UIntPtr.Zero);
                mouse_event(up, 0, 0, 0, UIntPtr.Zero);
                if (index + 1 < clickCount) Thread.Sleep(60);
            }
        }

        public static void DragMouse(int fromX, int fromY, int toX, int toY, string button, int durationMs)
        {
            MoveMouse(fromX, fromY);
            var (down, up) = MouseFlags(button);
            mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            try
            {
                var steps = Math.Clamp(durationMs / 12, 1, 120);
                for (var step = 1; step <= steps; step++)
                {
                    var ratio = step / (double)steps;
                    MoveMouse(
                        (int)Math.Round(fromX + (toX - fromX) * ratio),
                        (int)Math.Round(fromY + (toY - fromY) * ratio));
                    if (durationMs > 0) Thread.Sleep(Math.Max(1, durationMs / steps));
                }
            }
            finally { mouse_event(up, 0, 0, 0, UIntPtr.Zero); }
        }

        public static void ScrollMouse(int deltaX, int deltaY, int? x, int? y)
        {
            if (x is not null && y is not null) MoveMouse(x.Value, y.Value);
            if (deltaY != 0) mouse_event(MouseWheel, 0, 0, unchecked((uint)deltaY), UIntPtr.Zero);
            if (deltaX != 0) mouse_event(MouseHWheel, 0, 0, unchecked((uint)deltaX), UIntPtr.Zero);
        }

        public static void TypeText(string text)
        {
            foreach (var character in text)
            {
                var input = new[]
                {
                    KeyboardUnicode(character, keyUp: false),
                    KeyboardUnicode(character, keyUp: true)
                };
                if (SendInput((uint)input.Length, input, Marshal.SizeOf<Input>()) != input.Length)
                    throw new InvalidOperationException("Windows failed while entering text.");
            }
        }

        public static void PressKey(string key)
        {
            var vk = VirtualKey(key);
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KeyEventKeyUp, UIntPtr.Zero);
        }

        public static void PressShortcut(IReadOnlyList<string> keys)
        {
            var virtualKeys = keys.Select(VirtualKey).ToArray();
            try
            {
                foreach (var vk in virtualKeys) keybd_event(vk, 0, 0, UIntPtr.Zero);
            }
            finally
            {
                for (var index = virtualKeys.Length - 1; index >= 0; index--)
                    keybd_event(virtualKeys[index], 0, KeyEventKeyUp, UIntPtr.Zero);
            }
        }

        public static void FocusWindow(string id)
        {
            if (!long.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
                throw new ArgumentException("Invalid Windows window id.", nameof(id));
            var window = new IntPtr(raw);
            if (IsIconic(window)) _ = ShowWindow(window, SwRestore);
            if (!SetForegroundWindow(window)) throw new InvalidOperationException("Windows could not activate the selected window. The OS may be preventing foreground activation.");
        }

        private static (uint Down, uint Up) MouseFlags(string button) => button switch
        {
            "left" => (MouseLeftDown, MouseLeftUp),
            "right" => (MouseRightDown, MouseRightUp),
            "middle" => (MouseMiddleDown, MouseMiddleUp),
            _ => throw new UnreachableException()
        };

        private static byte VirtualKey(string key)
        {
            if (key.Length == 1)
            {
                var ch = key[0];
                if (ch is >= 'A' and <= 'Z') return (byte)ch;
                if (ch is >= '0' and <= '9') return (byte)ch;
            }
            return key switch
            {
                "ENTER" => 0x0D,
                "ESC" => 0x1B,
                "TAB" => 0x09,
                "SPACE" => 0x20,
                "BACKSPACE" => 0x08,
                "DELETE" => 0x2E,
                "INSERT" => 0x2D,
                "HOME" => 0x24,
                "END" => 0x23,
                "PAGEUP" => 0x21,
                "PAGEDOWN" => 0x22,
                "LEFT" => 0x25,
                "UP" => 0x26,
                "RIGHT" => 0x27,
                "DOWN" => 0x28,
                "SHIFT" => 0x10,
                "CTRL" => 0x11,
                "ALT" => 0x12,
                "CMD" => 0x5B,
                "F1" => 0x70,
                "F2" => 0x71,
                "F3" => 0x72,
                "F4" => 0x73,
                "F5" => 0x74,
                "F6" => 0x75,
                "F7" => 0x76,
                "F8" => 0x77,
                "F9" => 0x78,
                "F10" => 0x79,
                "F11" => 0x7A,
                "F12" => 0x7B,
                _ => throw new ArgumentException($"Unsupported Windows key '{key}'.", nameof(key))
            };
        }

        private static Input KeyboardUnicode(char character, bool keyUp)
            => new()
            {
                Type = 1,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = 0,
                        ScanCode = character,
                        Flags = KeyEventUnicode | (keyUp ? KeyEventKeyUp : 0)
                    }
                }
            };

        [StructLayout(LayoutKind.Sequential)]
        private struct Input { public uint Type; public InputUnion Union; }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MouseInput Mouse;
            [FieldOffset(0)] public KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            public int X; public int Y; public uint MouseData; public uint Flags; public uint Time; public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public ushort VirtualKey; public ushort ScanCode; public uint Flags; public uint Time; public UIntPtr ExtraInfo;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr window, int command);
    }

    [SupportedOSPlatform("macos")]
    private static class MacInput
    {
        private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
        private const uint HidEventTap = 0;
        private const uint MouseMoved = 5;
        private const uint LeftMouseDown = 1;
        private const uint LeftMouseUp = 2;
        private const uint RightMouseDown = 3;
        private const uint RightMouseUp = 4;
        private const uint LeftMouseDragged = 6;
        private const uint RightMouseDragged = 7;
        private const uint OtherMouseDown = 25;
        private const uint OtherMouseUp = 26;
        private const uint OtherMouseDragged = 27;
        private const uint KeyDown = 10;
        private const uint KeyUp = 11;
        private const uint ScrollUnitPixel = 0;
        private const uint MouseEventClickState = 1;

        public static void MoveMouse(int x, int y) => PostMouse(MouseMoved, new CGPoint(x, y), 0, 1);

        public static void ClickMouse(int x, int y, string button, int clickCount)
        {
            var (down, up, mouseButton) = MouseTypes(button, dragging: false);
            var point = new CGPoint(x, y);
            for (var click = 1; click <= clickCount; click++)
            {
                PostMouse(down, point, mouseButton, click);
                PostMouse(up, point, mouseButton, click);
                if (click < clickCount) Thread.Sleep(60);
            }
        }

        public static void DragMouse(int fromX, int fromY, int toX, int toY, string button, int durationMs)
        {
            var (down, up, mouseButton) = MouseTypes(button, dragging: false);
            var (_, _, _) = MouseTypes(button, dragging: true);
            var dragType = button switch { "left" => LeftMouseDragged, "right" => RightMouseDragged, _ => OtherMouseDragged };
            PostMouse(down, new CGPoint(fromX, fromY), mouseButton, 1);
            try
            {
                var steps = Math.Clamp(durationMs / 12, 1, 120);
                for (var step = 1; step <= steps; step++)
                {
                    var ratio = step / (double)steps;
                    PostMouse(dragType,
                        new CGPoint(fromX + (toX - fromX) * ratio, fromY + (toY - fromY) * ratio),
                        mouseButton,
                        1);
                    if (durationMs > 0) Thread.Sleep(Math.Max(1, durationMs / steps));
                }
            }
            finally { PostMouse(up, new CGPoint(toX, toY), mouseButton, 1); }
        }

        public static void ScrollMouse(int deltaX, int deltaY, int? x, int? y)
        {
            if (x is not null && y is not null) MoveMouse(x.Value, y.Value);
            var eventRef = CGEventCreateScrollWheelEvent(IntPtr.Zero, ScrollUnitPixel, 2, deltaY, deltaX);
            if (eventRef == IntPtr.Zero) throw new InvalidOperationException("macOS could not create a scroll event.");
            try { CGEventPost(HidEventTap, eventRef); }
            finally { CFRelease(eventRef); }
        }

        public static void TypeText(string text)
        {
            foreach (var chunk in ChunkText(text, 64))
            {
                PostUnicode(chunk, KeyDown);
                PostUnicode(chunk, KeyUp);
            }
        }

        public static void PressKey(string key)
        {
            var code = KeyCode(key);
            PostKey(code, KeyDown);
            PostKey(code, KeyUp);
        }

        public static void PressShortcut(IReadOnlyList<string> keys)
        {
            var codes = keys.Select(KeyCode).ToArray();
            try { foreach (var code in codes) PostKey(code, KeyDown); }
            finally { for (var index = codes.Length - 1; index >= 0; index--) PostKey(codes[index], KeyUp); }
        }

        public static void FocusApplication(int processId)
        {
            if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
            var script = $"ObjC.import('AppKit'); const app=$.NSRunningApplication.runningApplicationWithProcessIdentifier({processId}); if (!app) throw new Error('Application no longer exists'); if (!app.activateWithOptions(3)) throw new Error('macOS refused application activation');";
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/osascript")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                ArgumentList = { "-l", "JavaScript", "-e", script }
            }) ?? throw new InvalidOperationException("Failed to start macOS application activation helper.");
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("macOS could not activate the selected application: " + process.StandardError.ReadToEnd().Trim());
        }

        private static IEnumerable<string> ChunkText(string text, int length)
        {
            for (var offset = 0; offset < text.Length; offset += length)
                yield return text.Substring(offset, Math.Min(length, text.Length - offset));
        }

        private static void PostUnicode(string text, uint type)
        {
            var eventRef = CGEventCreateKeyboardEvent(IntPtr.Zero, 0, type == KeyDown);
            if (eventRef == IntPtr.Zero) throw new InvalidOperationException("macOS could not create a keyboard event.");
            try
            {
                var chars = text.ToCharArray();
                CGEventKeyboardSetUnicodeString(eventRef, (nuint)chars.Length, chars);
                CGEventPost(HidEventTap, eventRef);
            }
            finally { CFRelease(eventRef); }
        }

        private static void PostKey(ushort keyCode, uint type)
        {
            var eventRef = CGEventCreateKeyboardEvent(IntPtr.Zero, keyCode, type == KeyDown);
            if (eventRef == IntPtr.Zero) throw new InvalidOperationException("macOS could not create a keyboard event.");
            try { CGEventPost(HidEventTap, eventRef); }
            finally { CFRelease(eventRef); }
        }

        private static void PostMouse(uint type, CGPoint point, uint button, int clickCount)
        {
            var eventRef = CGEventCreateMouseEvent(IntPtr.Zero, type, point, button);
            if (eventRef == IntPtr.Zero) throw new InvalidOperationException("macOS could not create a mouse event.");
            try
            {
                if (clickCount > 1) CGEventSetIntegerValueField(eventRef, MouseEventClickState, clickCount);
                CGEventPost(HidEventTap, eventRef);
            }
            finally { CFRelease(eventRef); }
        }

        private static (uint Down, uint Up, uint Button) MouseTypes(string button, bool dragging)
            => button switch
            {
                "left" => (dragging ? LeftMouseDragged : LeftMouseDown, LeftMouseUp, 0),
                "right" => (dragging ? RightMouseDragged : RightMouseDown, RightMouseUp, 1),
                "middle" => (dragging ? OtherMouseDragged : OtherMouseDown, OtherMouseUp, 2),
                _ => throw new UnreachableException()
            };

        private static ushort KeyCode(string key)
        {
            if (key.Length == 1 && KeyCodes.TryGetValue(key[0], out var single)) return single;
            return key switch
            {
                "ENTER" => 36,
                "ESC" => 53,
                "TAB" => 48,
                "SPACE" => 49,
                "BACKSPACE" => 51,
                "DELETE" => 117,
                "INSERT" => 114,
                "HOME" => 115,
                "END" => 119,
                "PAGEUP" => 116,
                "PAGEDOWN" => 121,
                "LEFT" => 123,
                "RIGHT" => 124,
                "DOWN" => 125,
                "UP" => 126,
                "SHIFT" => 56,
                "CTRL" => 59,
                "ALT" => 58,
                "CMD" => 55,
                "F1" => 122,
                "F2" => 120,
                "F3" => 99,
                "F4" => 118,
                "F5" => 96,
                "F6" => 97,
                "F7" => 98,
                "F8" => 100,
                "F9" => 101,
                "F10" => 109,
                "F11" => 103,
                "F12" => 111,
                _ => throw new ArgumentException($"Unsupported macOS key '{key}'.", nameof(key))
            };
        }

        private static readonly Dictionary<char, ushort> KeyCodes = new()
        {
            ['A'] = 0, ['S'] = 1, ['D'] = 2, ['F'] = 3, ['H'] = 4, ['G'] = 5, ['Z'] = 6, ['X'] = 7,
            ['C'] = 8, ['V'] = 9, ['B'] = 11, ['Q'] = 12, ['W'] = 13, ['E'] = 14, ['R'] = 15, ['Y'] = 16, ['T'] = 17,
            ['1'] = 18, ['2'] = 19, ['3'] = 20, ['4'] = 21, ['6'] = 22, ['5'] = 23, ['9'] = 25, ['7'] = 26,
            ['8'] = 28, ['0'] = 29, ['O'] = 31, ['U'] = 32, ['I'] = 34, ['P'] = 35, ['L'] = 37, ['J'] = 38,
            ['K'] = 40, ['N'] = 45, ['M'] = 46
        };

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct CGPoint(double x, double y) { public readonly double X = x; public readonly double Y = y; }

        [DllImport(ApplicationServices)]
        private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint mouseType, CGPoint position, uint mouseButton);

        [DllImport(ApplicationServices)]
        private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort virtualKey, [MarshalAs(UnmanagedType.I1)] bool keyDown);

        [DllImport(ApplicationServices)]
        private static extern IntPtr CGEventCreateScrollWheelEvent(IntPtr source, uint units, uint wheelCount, int wheel1, int wheel2);

        [DllImport(ApplicationServices)]
        private static extern void CGEventKeyboardSetUnicodeString(IntPtr eventRef, nuint length, [In] char[] unicodeString);

        [DllImport(ApplicationServices)]
        private static extern void CGEventSetIntegerValueField(IntPtr eventRef, uint field, long value);

        [DllImport(ApplicationServices)]
        private static extern void CGEventPost(uint tap, IntPtr eventRef);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        private static extern void CFRelease(IntPtr value);
    }
}
