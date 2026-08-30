using System.Security.Cryptography;
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

        var token = "matemcp_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var initial = new
        {
            Mate = new
            {
                BindAddress = "127.0.0.1",
                Port = 45871,
                AllowInsecureHttp = true,
                AccessToken = token,
                CertificatePath = (string?)null,
                CertificatePassword = (string?)null,
                RequireShellApproval = true,
                ApprovalTimeoutSeconds = 120,
                Projects = Array.Empty<object>()
            }
        };

        var json = JsonSerializer.Serialize(initial, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json + Environment.NewLine);
        TryRestrictPermissions(path);
        return path;
    }

    private static void TryRestrictPermissions(string path)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Best effort. Startup validation still prevents using the placeholder token.
        }
    }
}
