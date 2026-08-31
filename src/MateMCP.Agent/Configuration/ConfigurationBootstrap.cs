using System.Text.Json;

namespace MateMCP.Agent.Configuration;

public static class ConfigurationBootstrap
{
    public static string EnsureUserConfiguration()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MateMCP");
        Directory.CreateDirectory(directory);

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
                    DeviceId = (string?)null
                },
                Projects = Array.Empty<object>()
            }
        };

        var json = JsonSerializer.Serialize(initial, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json + Environment.NewLine);
        TryRestrictPermissions(path);
        return path;
    }

    public static void TryRestrictPermissions(string path)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best effort. Sensitive values are not expected in this file.
        }
    }
}
