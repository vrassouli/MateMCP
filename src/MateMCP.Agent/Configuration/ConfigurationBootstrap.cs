using System.Runtime.InteropServices;
using System.Text.Json;

namespace MateMCP.Agent.Configuration;

public static class ConfigurationBootstrap
{
    public static string EnsureUserConfiguration()
    {
        var directory = GetUserDataDirectory();
        Directory.CreateDirectory(directory);
        TryRestoreDelegatedMacOwnership(directory);

        var path = Path.Combine(directory, "appsettings.json");
        if (File.Exists(path)) return path;

        var initial = new
        {
            Mate = new
            {
                BindAddress = "127.0.0.1",
                Port = 45871,
                AllowInsecureHttp = true,
                CertificatePath = (string?)null,
                CertificatePassword = (string?)null,
                RequireShellApproval = true,
                ApprovalTimeoutSeconds = 120,
                Relay = new
                {
                    Enabled = true,
                    Url = "https://relay.matemcp.com",
                    ControlPlaneUrl = "https://api.matemcp.com",
                    DeviceId = (string?)null,
                    EnrollmentSuppressed = false
                },
                Projects = Array.Empty<object>()
            }
        };

        var json = JsonSerializer.Serialize(initial, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json + Environment.NewLine);
        TryRestrictPermissions(path);
        return path;
    }

    public static string GetUserDataDirectory()
    {
        if (OperatingSystem.IsMacOS())
        {
            var delegatedHome = Environment.GetEnvironmentVariable("MATEMCP_MAC_USER_HOME");
            if (!string.IsNullOrWhiteSpace(delegatedHome))
                return Path.Combine(delegatedHome, "Library", "Application Support", "MateMCP");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MateMCP");
    }

    public static void TryRestrictPermissions(string path)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            TryRestoreDelegatedMacOwnership(path);
        }
        catch
        {
            // Best effort. Sensitive values are not expected in this file.
        }
    }

    public static void TryRestoreDelegatedMacOwnership(string path)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var uidText = Environment.GetEnvironmentVariable("MATEMCP_MAC_USER_UID");
        if (!uint.TryParse(uidText, out var uid)) return;
        try { _ = Chown(path, uid, uint.MaxValue); }
        catch { }
    }

    [DllImport("libc", EntryPoint = "chown", SetLastError = true)]
    private static extern int Chown(string path, uint owner, uint group);
}
