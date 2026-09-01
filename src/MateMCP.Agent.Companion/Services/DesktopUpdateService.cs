using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace MateMCP.Agent.Companion.Services;

public sealed class DesktopUpdateService : IDisposable
{
    private const string ReleaseApi = "https://api.github.com/repos/vrassouli/MateMCP/releases/tags/agent-latest";
    private const string WindowsBootstrap = "https://raw.githubusercontent.com/vrassouli/MateMCP/main/scripts/bootstrap-windows.ps1";
    private const string MacBootstrap = "https://raw.githubusercontent.com/vrassouli/MateMCP/main/scripts/bootstrap-macos.sh";
    private const string AutoUpdatePreference = "matemcp.desktop.auto-update";
    private const string MarkerFileName = ".desktop-release-asset";

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public DesktopUpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MateMCP-Agent-Companion/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public bool AutoUpdateEnabled
    {
        get => Preferences.Default.Get(AutoUpdatePreference, false);
        set => Preferences.Default.Set(AutoUpdatePreference, value);
    }

    public async Task<DesktopUpdateStatus> CheckAsync(CancellationToken ct = default)
    {
        var assetName = GetAssetName();
        if (assetName is null)
            return new DesktopUpdateStatus(false, false, null, null, "Automatic updates are not available for this architecture yet.");

        var release = await _http.GetFromJsonAsync<GitHubRelease>(ReleaseApi, ct);
        var asset = release?.Assets.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
            return new DesktopUpdateStatus(true, false, null, null, $"Release asset {assetName} is not available.");

        var markerPath = GetMarkerPath();
        var installedAssetId = ReadInstalledAssetId(markerPath);
        if (installedAssetId == 0)
        {
            // First launch after introducing update tracking: the Companion being
            // executed came from the current public Desktop package, so establish
            // that release asset as the baseline without reinstalling it.
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            File.WriteAllText(markerPath, asset.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            installedAssetId = asset.Id;
        }

        return new DesktopUpdateStatus(
            Supported: true,
            UpdateAvailable: installedAssetId != asset.Id,
            AssetId: asset.Id,
            UpdatedAt: asset.UpdatedAt,
            Message: installedAssetId == asset.Id ? "MateMCP Desktop is up to date." : "A newer MateMCP Desktop build is available.");
    }

    public void BeginUpdate(long assetId)
    {
        if (assetId <= 0) throw new ArgumentOutOfRangeException(nameof(assetId));
        var markerPath = GetMarkerPath();

        if (OperatingSystem.IsWindows())
        {
            var escapedMarker = markerPath.Replace("'", "''", StringComparison.Ordinal);
            var script = $"$ErrorActionPreference='Stop'; Start-Sleep -Seconds 2; irm '{WindowsBootstrap}' | iex; [IO.File]::WriteAllText('{escapedMarker}','{assetId}')";
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{script}\""
            });
            Environment.Exit(0);
        }

        if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
        {
            var escapedMarker = markerPath.Replace("'", "'\"'\"'", StringComparison.Ordinal);
            var command = $"sleep 2; if curl -fsSL '{MacBootstrap}' | /bin/bash >/tmp/matemcp-update.log 2>&1; then printf '%s' '{assetId}' > '{escapedMarker}'; fi";
            Process.Start(new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-c", command }
            });
            Environment.Exit(0);
        }

        throw new PlatformNotSupportedException("MateMCP Desktop self-update is supported on Windows and macOS.");
    }

    private static long ReadInstalledAssetId(string markerPath)
    {
        try
        {
            return File.Exists(markerPath) && long.TryParse(File.ReadAllText(markerPath).Trim(), out var value) ? value : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string GetMarkerPath()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MateMCP", MarkerFileName);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "Application Support", "MateMCP", MarkerFileName);
    }

    private static string? GetAssetName()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return "MateMCP-Desktop-win-x64.zip";
        if ((OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS()) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return "MateMCP-Desktop-macos-arm64.tar.gz";
        return null;
    }

    public void Dispose() => _http.Dispose();

    private sealed record GitHubRelease([property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets);
    private sealed record GitHubAsset(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);
}

public sealed record DesktopUpdateStatus(bool Supported, bool UpdateAvailable, long? AssetId, DateTimeOffset? UpdatedAt, string Message);
