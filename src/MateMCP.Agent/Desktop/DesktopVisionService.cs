using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace MateMCP.Agent.Desktop;

public sealed record DesktopScreenInfo(
    string Id,
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    double Scale,
    bool Primary);

public sealed record DesktopWindowInfo(
    string Id,
    string Title,
    string Application,
    int ProcessId,
    int X,
    int Y,
    int Width,
    int Height,
    bool Active,
    bool Minimized);

public sealed record DesktopCaptureResult(
    byte[] Bytes,
    string MimeType,
    int Width,
    int Height,
    string Target,
    string? TargetId);

public sealed class DesktopVisionService
{
    public const int MaxCaptureBytes = 16 * 1024 * 1024;
    private const int MaxCaptureDimension = 16_384;
    private const long MaxCapturePixels = 120_000_000;

    public Task<IReadOnlyList<DesktopScreenInfo>> ListScreensAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows()) return Task.FromResult<IReadOnlyList<DesktopScreenInfo>>(WindowsDesktopVision.ListScreens());
        if (OperatingSystem.IsMacOS()) return Task.FromResult<IReadOnlyList<DesktopScreenInfo>>(MacDesktopVision.ListScreens());
        throw UnsupportedPlatform();
    }

    public async Task<IReadOnlyList<DesktopWindowInfo>> ListWindowsAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows()) return WindowsDesktopVision.ListWindows();
        if (OperatingSystem.IsMacOS()) return await MacDesktopVision.ListWindowsAsync(cancellationToken);
        throw UnsupportedPlatform();
    }

    public async Task<DesktopCaptureResult> CaptureAsync(
        string target,
        string? id = null,
        int? x = null,
        int? y = null,
        int? width = null,
        int? height = null,
        CancellationToken cancellationToken = default)
    {
        target = NormalizeTarget(target);
        if (OperatingSystem.IsWindows())
            return await WindowsDesktopVision.CaptureAsync(target, id, x, y, width, height, cancellationToken);
        if (OperatingSystem.IsMacOS())
            return await MacDesktopVision.CaptureAsync(target, id, x, y, width, height, cancellationToken);
        throw UnsupportedPlatform();
    }

    public static string NormalizeTarget(string target)
    {
        target = (target ?? string.Empty).Trim().ToLowerInvariant();
        return target switch
        {
            "screen" or "window" or "region" => target,
            _ => throw new ArgumentException("target must be one of: screen, window, region.", nameof(target))
        };
    }

    internal static void ValidateRegion(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Capture width and height must be positive.");
        if (width > MaxCaptureDimension || height > MaxCaptureDimension || (long)width * height > MaxCapturePixels)
            throw new ArgumentOutOfRangeException(nameof(width), "Requested capture region is too large.");
    }

    private static PlatformNotSupportedException UnsupportedPlatform()
        => new("Desktop vision currently supports Windows and macOS Agents.");

    private static DesktopCaptureResult ReadCapture(string path, string target, string? targetId)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length == 0) throw new InvalidOperationException("The operating system returned an empty screenshot.");
        if (bytes.Length > MaxCaptureBytes)
            throw new InvalidOperationException($"Screenshot is {bytes.Length} bytes; the current maximum is {MaxCaptureBytes} bytes.");
        var (width, height) = ReadPngDimensions(bytes);
        return new DesktopCaptureResult(bytes, "image/png", width, height, target, targetId);
    }

    private static (int Width, int Height) ReadPngDimensions(ReadOnlySpan<byte> png)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (png.Length < 24 || !png[..8].SequenceEqual(signature))
            throw new InvalidDataException("Screenshot encoder did not produce a PNG image.");

        var width = ReadBigEndianInt32(png.Slice(16, 4));
        var height = ReadBigEndianInt32(png.Slice(20, 4));
        if (width <= 0 || height <= 0) throw new InvalidDataException("Screenshot PNG has invalid dimensions.");
        return (width, height);
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> value)
        => (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var item in environment) startInfo.Environment[item.Key] = item.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    [SupportedOSPlatform("windows")]
    private static class WindowsDesktopVision
    {
        private const uint MonitorInfoPrimary = 0x00000001;
        private const int MonitorDefaultDpi = 96;
        private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

        public static IReadOnlyList<DesktopScreenInfo> ListScreens()
        {
            using var dpi = EnterPerMonitorDpiAwareness();
            var screens = new List<DesktopScreenInfo>();
            MonitorEnumProc callback = (monitor, _, _, _) =>
            {
                var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>(), DeviceName = string.Empty };
                if (!GetMonitorInfo(monitor, ref info)) return true;
                var bounds = info.Monitor;
                var scale = TryGetScale(monitor);
                screens.Add(new DesktopScreenInfo(
                    info.DeviceName,
                    info.DeviceName,
                    bounds.Left,
                    bounds.Top,
                    bounds.Right - bounds.Left,
                    bounds.Bottom - bounds.Top,
                    scale,
                    (info.Flags & MonitorInfoPrimary) != 0));
                return true;
            };

            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero))
                throw new InvalidOperationException("Windows failed to enumerate displays.");
            return screens.OrderByDescending(screen => screen.Primary).ThenBy(screen => screen.X).ThenBy(screen => screen.Y).ToArray();
        }

        public static IReadOnlyList<DesktopWindowInfo> ListWindows()
        {
            using var dpi = EnterPerMonitorDpiAwareness();
            var windows = new List<DesktopWindowInfo>();
            var foreground = GetForegroundWindow();
            EnumWindowsProc callback = (window, _) =>
            {
                if (!IsWindowVisible(window)) return true;
                var titleLength = GetWindowTextLength(window);
                if (titleLength <= 0) return true;

                var titleBuilder = new StringBuilder(titleLength + 1);
                _ = GetWindowText(window, titleBuilder, titleBuilder.Capacity);
                var title = titleBuilder.ToString().Trim();
                if (title.Length == 0 || !GetWindowRect(window, out var rect)) return true;
                var width = rect.Right - rect.Left;
                var height = rect.Bottom - rect.Top;
                if (width <= 1 || height <= 1) return true;

                GetWindowThreadProcessId(window, out var processId);
                var application = ResolveProcessName(processId);
                windows.Add(new DesktopWindowInfo(
                    window.ToInt64().ToString("X", CultureInfo.InvariantCulture),
                    title,
                    application,
                    unchecked((int)processId),
                    rect.Left,
                    rect.Top,
                    width,
                    height,
                    window == foreground,
                    IsIconic(window)));
                return true;
            };

            if (!EnumWindows(callback, IntPtr.Zero))
                throw new InvalidOperationException("Windows failed to enumerate application windows.");
            return windows.OrderByDescending(window => window.Active).ThenBy(window => window.Application, StringComparer.OrdinalIgnoreCase).ThenBy(window => window.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        public static async Task<DesktopCaptureResult> CaptureAsync(
            string target,
            string? id,
            int? x,
            int? y,
            int? width,
            int? height,
            CancellationToken cancellationToken)
        {
            using var dpi = EnterPerMonitorDpiAwareness();
            var region = target switch
            {
                "screen" => ResolveScreen(id),
                "window" => ResolveWindow(id),
                "region" => ResolveRegion(x, y, width, height),
                _ => throw new UnreachableException()
            };
            ValidateRegion(region.Width, region.Height);

            var temp = Path.Combine(Path.GetTempPath(), $"matemcp-capture-{Guid.NewGuid():N}.png");
            try
            {
                const string script = "$ErrorActionPreference='Stop'; " +
                    "Add-Type -AssemblyName System.Drawing; " +
                    "Add-Type @'\nusing System; using System.Runtime.InteropServices; public static class MateMcpDpi { [DllImport(\"user32.dll\")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value); }\n'@; " +
                    "[void][MateMcpDpi]::SetProcessDpiAwarenessContext([IntPtr](-4)); " +
                    "$x=[int]$env:MATEMCP_X; $y=[int]$env:MATEMCP_Y; $w=[int]$env:MATEMCP_W; $h=[int]$env:MATEMCP_H; " +
                    "$bmp=New-Object System.Drawing.Bitmap($w,$h,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb); " +
                    "$g=[System.Drawing.Graphics]::FromImage($bmp); try { $g.CopyFromScreen($x,$y,0,0,$bmp.Size,[System.Drawing.CopyPixelOperation]::SourceCopy); $bmp.Save($env:MATEMCP_OUT,[System.Drawing.Imaging.ImageFormat]::Png) } finally { $g.Dispose(); $bmp.Dispose() }";

                var result = await RunProcessAsync(
                    "powershell.exe",
                    ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
                    new Dictionary<string, string?>
                    {
                        ["MATEMCP_X"] = region.X.ToString(CultureInfo.InvariantCulture),
                        ["MATEMCP_Y"] = region.Y.ToString(CultureInfo.InvariantCulture),
                        ["MATEMCP_W"] = region.Width.ToString(CultureInfo.InvariantCulture),
                        ["MATEMCP_H"] = region.Height.ToString(CultureInfo.InvariantCulture),
                        ["MATEMCP_OUT"] = temp
                    },
                    cancellationToken);
                if (result.ExitCode != 0 || !File.Exists(temp))
                    throw new InvalidOperationException($"Windows screenshot capture failed: {CompactError(result.Stderr)}");
                return ReadCapture(temp, target, id);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static CaptureRegion ResolveScreen(string? id)
        {
            var screens = ListScreens();
            var screen = string.IsNullOrWhiteSpace(id)
                ? screens.FirstOrDefault(item => item.Primary) ?? screens.FirstOrDefault()
                : screens.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (screen is null) throw new KeyNotFoundException($"Screen '{id ?? "primary"}' was not found.");
            return new CaptureRegion(screen.X, screen.Y, screen.Width, screen.Height);
        }

        private static CaptureRegion ResolveWindow(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A window id from window_list is required when target=window.", nameof(id));
            if (!long.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
                throw new ArgumentException("Invalid Windows window id.", nameof(id));
            var window = new IntPtr(raw);
            if (IsIconic(window)) throw new InvalidOperationException("The selected window is minimized. Restore it before capture.");
            if (!GetWindowRect(window, out var rect)) throw new KeyNotFoundException($"Window '{id}' is no longer available.");
            return new CaptureRegion(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        private static CaptureRegion ResolveRegion(int? x, int? y, int? width, int? height)
        {
            if (x is null || y is null || width is null || height is null)
                throw new ArgumentException("x, y, width, and height are required when target=region.");
            return new CaptureRegion(x.Value, y.Value, width.Value, height.Value);
        }

        private static string ResolveProcessName(uint processId)
        {
            try { using var process = Process.GetProcessById(unchecked((int)processId)); return process.ProcessName; }
            catch { return $"pid:{processId}"; }
        }

        private static double TryGetScale(IntPtr monitor)
        {
            try
            {
                return GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0
                    ? Math.Round(dpiX / (double)MonitorDefaultDpi, 3)
                    : 1d;
            }
            catch (DllNotFoundException) { return 1d; }
            catch (EntryPointNotFoundException) { return 1d; }
        }

        private static IDisposable EnterPerMonitorDpiAwareness()
        {
            try
            {
                var previous = SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
                return new DpiAwarenessScope(previous);
            }
            catch (EntryPointNotFoundException) { return NoopDisposable.Instance; }
        }

        private sealed class DpiAwarenessScope(IntPtr previous) : IDisposable
        {
            public void Dispose()
            {
                if (previous == IntPtr.Zero) return;
                try { _ = SetThreadDpiAwarenessContext(previous); } catch { }
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();
            public void Dispose() { }
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }

    [SupportedOSPlatform("macos")]
    private static class MacDesktopVision
    {
        private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

        public static IReadOnlyList<DesktopScreenInfo> ListScreens()
        {
            var displays = new uint[64];
            var error = CGGetActiveDisplayList((uint)displays.Length, displays, out var count);
            if (error != 0) throw new InvalidOperationException($"CoreGraphics failed to enumerate displays (error {error}).");
            var primary = CGMainDisplayID();
            var result = new List<DesktopScreenInfo>((int)count);
            for (var index = 0; index < count; index++)
            {
                var display = displays[index];
                var bounds = CGDisplayBounds(display);
                var logicalWidth = Math.Max(1, (int)Math.Round(bounds.Size.Width));
                var pixelWidth = (double)CGDisplayPixelsWide(display);
                result.Add(new DesktopScreenInfo(
                    display.ToString(CultureInfo.InvariantCulture),
                    display == primary ? "Main display" : $"Display {display}",
                    (int)Math.Round(bounds.Origin.X),
                    (int)Math.Round(bounds.Origin.Y),
                    logicalWidth,
                    Math.Max(1, (int)Math.Round(bounds.Size.Height)),
                    Math.Round(pixelWidth / logicalWidth, 3),
                    display == primary));
            }
            return result.OrderByDescending(screen => screen.Primary).ThenBy(screen => screen.X).ThenBy(screen => screen.Y).ToArray();
        }

        public static async Task<IReadOnlyList<DesktopWindowInfo>> ListWindowsAsync(CancellationToken cancellationToken)
        {
            const string script = "ObjC.import('CoreGraphics'); ObjC.import('AppKit'); " +
                "let raw=$.CGWindowListCopyWindowInfo($.kCGWindowListOptionOnScreenOnly | $.kCGWindowListExcludeDesktopElements,$.kCGNullWindowID); " +
                "try { ObjC.bindFunction('CFMakeCollectable',['id',['void *']]); raw=$.CFMakeCollectable(raw); } catch(e) {} " +
                "const all=ObjC.deepUnwrap(raw)||[]; const front=Number($.NSWorkspace.sharedWorkspace.frontmostApplication.processIdentifier); " +
                "const rows=all.filter(w=>Number(w.kCGWindowLayer)===0 && w.kCGWindowBounds && Number(w.kCGWindowOwnerPID)>0 && Number(w.kCGWindowNumber)>0)" +
                ".map(w=>({id:String(w.kCGWindowNumber),title:String(w.kCGWindowName||''),application:String(w.kCGWindowOwnerName||''),processId:Number(w.kCGWindowOwnerPID),x:Number(w.kCGWindowBounds.X||0),y:Number(w.kCGWindowBounds.Y||0),width:Number(w.kCGWindowBounds.Width||0),height:Number(w.kCGWindowBounds.Height||0),active:Number(w.kCGWindowOwnerPID)===front,minimized:false}))" +
                ".filter(w=>w.width>1 && w.height>1); JSON.stringify(rows);";

            var process = await RunProcessAsync("/usr/bin/osascript", ["-l", "JavaScript", "-e", script], null, cancellationToken);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"macOS window enumeration failed: {CompactError(process.Stderr)}");
            try
            {
                return JsonSerializer.Deserialize<DesktopWindowInfo[]>(process.Stdout, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("macOS returned an unreadable window list.", ex);
            }
        }

        public static async Task<DesktopCaptureResult> CaptureAsync(
            string target,
            string? id,
            int? x,
            int? y,
            int? width,
            int? height,
            CancellationToken cancellationToken)
        {
            var temp = Path.Combine(Path.GetTempPath(), $"matemcp-capture-{Guid.NewGuid():N}.png");
            try
            {
                var arguments = new List<string> { "-x", "-t", "png" };
                switch (target)
                {
                    case "screen":
                    {
                        var screen = ResolveScreen(id);
                        ValidateRegion(screen.Width, screen.Height);
                        arguments.Add("-R");
                        arguments.Add($"{screen.X},{screen.Y},{screen.Width},{screen.Height}");
                        break;
                    }
                    case "window":
                        if (string.IsNullOrWhiteSpace(id) || !uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                            throw new ArgumentException("A numeric window id from window_list is required when target=window.", nameof(id));
                        arguments.Add("-l");
                        arguments.Add(id);
                        break;
                    case "region":
                        if (x is null || y is null || width is null || height is null)
                            throw new ArgumentException("x, y, width, and height are required when target=region.");
                        ValidateRegion(width.Value, height.Value);
                        arguments.Add("-R");
                        arguments.Add($"{x.Value},{y.Value},{width.Value},{height.Value}");
                        break;
                    default:
                        throw new UnreachableException();
                }
                arguments.Add(temp);

                var process = await RunProcessAsync("/usr/sbin/screencapture", arguments, null, cancellationToken);
                if (process.ExitCode != 0 || !File.Exists(temp))
                {
                    var detail = CompactError(process.Stderr);
                    throw new InvalidOperationException(
                        "macOS screenshot capture failed. Ensure MateMCP has Screen Recording permission in System Settings > Privacy & Security > Screen & System Audio Recording." +
                        (detail.Length == 0 ? string.Empty : $" OS detail: {detail}"));
                }
                return ReadCapture(temp, target, id);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        private static DesktopScreenInfo ResolveScreen(string? id)
        {
            var screens = ListScreens();
            var screen = string.IsNullOrWhiteSpace(id)
                ? screens.FirstOrDefault(item => item.Primary) ?? screens.FirstOrDefault()
                : screens.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            return screen ?? throw new KeyNotFoundException($"Screen '{id ?? "primary"}' was not found.");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CGPoint { public double X; public double Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct CGSize { public double Width; public double Height; }
        [StructLayout(LayoutKind.Sequential)]
        private struct CGRect { public CGPoint Origin; public CGSize Size; }

        [DllImport(CoreGraphics)]
        private static extern int CGGetActiveDisplayList(uint maxDisplays, [Out] uint[] activeDisplays, out uint displayCount);

        [DllImport(CoreGraphics)]
        private static extern uint CGMainDisplayID();

        [DllImport(CoreGraphics)]
        private static extern CGRect CGDisplayBounds(uint display);

        [DllImport(CoreGraphics)]
        private static extern nuint CGDisplayPixelsWide(uint display);
    }

    private static string CompactError(string value)
    {
        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= 500 ? compact : compact[..500];
    }

    private sealed record CaptureRegion(int X, int Y, int Width, int Height);
}
