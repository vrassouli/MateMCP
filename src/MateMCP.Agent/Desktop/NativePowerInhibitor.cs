using System.Runtime.InteropServices;

namespace MateMCP.Agent.Desktop;

public interface IPowerInhibitor : IDisposable
{
    bool Supported { get; }
    bool IsActive { get; }
    string? LastError { get; }
    bool Acquire();
    void Release();
}

public sealed class NativePowerInhibitor : IPowerInhibitor
{
    private const uint ReasonContextVersion = 0;
    private const uint ReasonContextSimpleString = 0x1;
    private const int PowerRequestSystemRequired = 1;
    private const uint IopmAssertionLevelOn = 255;
    private const uint Utf8Encoding = 0x08000100;

    private readonly object _gate = new();
    private IntPtr _windowsRequest;
    private uint _macAssertionId;

    public bool Supported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    public bool IsActive { get; private set; }
    public string? LastError { get; private set; }

    public bool Acquire()
    {
        lock (_gate)
        {
            if (IsActive) return true;
            LastError = null;
            if (!Supported)
            {
                LastError = "System sleep inhibition is not supported on this platform yet.";
                return false;
            }

            try
            {
                if (OperatingSystem.IsWindows())
                    return AcquireWindows();
                if (OperatingSystem.IsMacOS())
                    return AcquireMac();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                ReleaseCore();
            }

            return false;
        }
    }

    public void Release()
    {
        lock (_gate)
            ReleaseCore();
    }

    private bool AcquireWindows()
    {
        var reason = Marshal.StringToHGlobalUni("MateMCP is actively performing user-requested work.");
        try
        {
            var context = new ReasonContext
            {
                Version = ReasonContextVersion,
                Flags = ReasonContextSimpleString,
                Reason = reason
            };
            _windowsRequest = PowerCreateRequest(ref context);
            if (_windowsRequest == IntPtr.Zero || _windowsRequest == new IntPtr(-1))
            {
                LastError = $"PowerCreateRequest failed with Win32 error {Marshal.GetLastWin32Error()}.";
                _windowsRequest = IntPtr.Zero;
                return false;
            }

            if (!PowerSetRequest(_windowsRequest, PowerRequestSystemRequired))
            {
                LastError = $"PowerSetRequest failed with Win32 error {Marshal.GetLastWin32Error()}.";
                CloseHandle(_windowsRequest);
                _windowsRequest = IntPtr.Zero;
                return false;
            }

            IsActive = true;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(reason);
        }
    }

    private bool AcquireMac()
    {
        var assertionType = CFStringCreateWithCString(IntPtr.Zero, "PreventUserIdleSystemSleep", Utf8Encoding);
        var reason = CFStringCreateWithCString(IntPtr.Zero, "MateMCP is actively performing user-requested work.", Utf8Encoding);
        if (assertionType == IntPtr.Zero || reason == IntPtr.Zero)
        {
            if (assertionType != IntPtr.Zero) CFRelease(assertionType);
            if (reason != IntPtr.Zero) CFRelease(reason);
            LastError = "Could not create macOS power assertion strings.";
            return false;
        }

        try
        {
            var result = IOPMAssertionCreateWithName(assertionType, IopmAssertionLevelOn, reason, out _macAssertionId);
            if (result != 0)
            {
                LastError = $"IOPMAssertionCreateWithName failed with IOKit status 0x{result:X8}.";
                _macAssertionId = 0;
                return false;
            }

            IsActive = true;
            return true;
        }
        finally
        {
            CFRelease(reason);
            CFRelease(assertionType);
        }
    }

    private void ReleaseCore()
    {
        if (OperatingSystem.IsWindows() && _windowsRequest != IntPtr.Zero)
        {
            if (IsActive)
                PowerClearRequest(_windowsRequest, PowerRequestSystemRequired);
            CloseHandle(_windowsRequest);
            _windowsRequest = IntPtr.Zero;
        }
        else if (OperatingSystem.IsMacOS() && _macAssertionId != 0)
        {
            IOPMAssertionRelease(_macAssertionId);
            _macAssertionId = 0;
        }

        IsActive = false;
    }

    public void Dispose() => Release();

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr Reason;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerSetRequest(IntPtr powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PowerClearRequest(IntPtr powerRequest, int requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string value, uint encoding);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr value);

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern uint IOPMAssertionCreateWithName(IntPtr assertionType, uint assertionLevel, IntPtr assertionName, out uint assertionId);

    [DllImport("/System/Library/Frameworks/IOKit.framework/IOKit")]
    private static extern uint IOPMAssertionRelease(uint assertionId);
}
