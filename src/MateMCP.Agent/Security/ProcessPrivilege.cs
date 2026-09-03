using System.Runtime.InteropServices;
using System.Security.Principal;

namespace MateMCP.Agent.Security;

public static class ProcessPrivilege
{
    public static bool IsElevated()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            return GetEffectiveUserId() == 0;

        return false;
    }

    public static string Identity
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return WindowsIdentity.GetCurrent().Name ?? Environment.UserName;
            return Environment.UserName;
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
}
