using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace MateMCP.Agent.Companion.Services;

public sealed class DesktopUpdateService : IDisposable
{
    private const string ReleaseApi = "https://api.github.com/repos/vrassouli/MateMCP/releases/tags/agent-latest";
    private const string AutoUpdatePreference = "matemcp.desktop.auto-update";
    private const string MarkerFileName = ".desktop-release-asset";
    private const string FailureFileName = ".desktop-update-error";

    private readonly HttpClient _http = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public DesktopUpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("MateMCP-Agent-Companion/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public bool AutoUpdateEnabled
    {
        get => Microsoft.Maui.Storage.Preferences.Default.Get(AutoUpdatePreference, false);
        set => Microsoft.Maui.Storage.Preferences.Default.Set(AutoUpdatePreference, value);
    }

    public async Task<DesktopUpdateStatus> CheckAsync(CancellationToken ct = default)
    {
        var assetName = GetAssetName();
        var lastFailure = ReadLastFailure();
        if (assetName is null)
            return new DesktopUpdateStatus(false, false, null, null, "Automatic updates are not available for this architecture yet.", lastFailure);

        var release = await GetReleaseAsync(ct);
        var asset = release?.Assets.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
            return new DesktopUpdateStatus(true, false, null, null, $"Release asset {assetName} is not available.", lastFailure);

        var markerPath = GetMarkerPath();
        var installedAssetId = ReadInstalledAssetId(markerPath);
        if (installedAssetId == 0)
        {
            // First launch after introducing update tracking: the Companion being
            // executed came from the current public Desktop package, so establish
            // that release asset as the baseline without reinstalling it.
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            File.WriteAllText(markerPath, asset.Id.ToString(CultureInfo.InvariantCulture));
            installedAssetId = asset.Id;
        }

        return new DesktopUpdateStatus(
            Supported: true,
            UpdateAvailable: installedAssetId != asset.Id,
            AssetId: asset.Id,
            UpdatedAt: asset.UpdatedAt,
            Message: installedAssetId == asset.Id ? "MateMCP Desktop is up to date." : "A newer MateMCP Desktop build is available.",
            LastFailure: lastFailure);
    }

    public async Task BeginUpdateAsync(
        long assetId,
        IProgress<DesktopUpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (assetId <= 0) throw new ArgumentOutOfRangeException(nameof(assetId));

        var assetName = GetAssetName() ?? throw new PlatformNotSupportedException("MateMCP Desktop self-update is supported on Windows x64 and Apple Silicon macOS.");
        progress?.Report(new DesktopUpdateProgress("Preparing", "Preparing update...", 0, null));

        var release = await GetReleaseAsync(ct)
            ?? throw new InvalidOperationException("Could not read the MateMCP Desktop release metadata.");
        var asset = release.Assets.FirstOrDefault(a => a.Id == assetId && string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Release asset {assetName} is no longer available. Check for updates again.");

        var tempRoot = Path.Combine(Path.GetTempPath(), "matemcp-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var archivePath = Path.Combine(tempRoot, asset.Name);

        try
        {
            await DownloadAssetAsync(asset, archivePath, progress, ct);
            progress?.Report(new DesktopUpdateProgress("Installing", "Download complete. Restarting Companion to install the update...", asset.Size, asset.Size));
            LaunchInstaller(tempRoot, archivePath, assetId);
        }
        catch
        {
            TryDeleteDirectory(tempRoot);
            throw;
        }

        Environment.Exit(0);
    }

    private async Task<GitHubRelease?> GetReleaseAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            return await _http.GetFromJsonAsync<GitHubRelease>(ReleaseApi, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Update check timed out. Check your network connection and try again.");
        }
    }

    private async Task DownloadAssetAsync(
        GitHubAsset asset,
        string archivePath,
        IProgress<DesktopUpdateProgress>? progress,
        CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            using var response = await _http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : null);
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
            await using var target = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long received = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), timeout.Token);
                if (read == 0) break;
                await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                received += read;
                progress?.Report(new DesktopUpdateProgress("Downloading", "Downloading update...", received, totalBytes));
            }

            await target.FlushAsync(timeout.Token);
            if (totalBytes is > 0 && received != totalBytes)
                throw new IOException($"Update download was incomplete ({received} of {totalBytes} bytes received).");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Update download timed out. Check your network connection and try again.");
        }
    }

    private static void LaunchInstaller(string tempRoot, string archivePath, long assetId)
    {
        var markerPath = GetMarkerPath();
        var failurePath = GetFailurePath();
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);

        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(tempRoot, "install-update.ps1");
            File.WriteAllText(scriptPath, BuildWindowsInstallScript(tempRoot, archivePath, markerPath, failurePath, assetId));
            var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\""
            });
            if (process is null) throw new InvalidOperationException("Could not start the Windows update installer.");
            return;
        }

        if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
        {
            var scriptPath = Path.Combine(tempRoot, "install-update.sh");
            File.WriteAllText(scriptPath, BuildMacInstallScript(tempRoot, archivePath, markerPath, failurePath, assetId));
            var process = Process.Start(new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { scriptPath }
            });
            if (process is null) throw new InvalidOperationException("Could not start the macOS update installer.");
            return;
        }

        throw new PlatformNotSupportedException("MateMCP Desktop self-update is supported on Windows and macOS.");
    }

    private static string BuildMacInstallScript(string tempRoot, string archivePath, string markerPath, string failurePath, long assetId)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var logPath = Path.Combine(home, "Library", "Logs", "MateMCP", "update.log");
        var companionPath = Path.Combine(home, "Applications", "MateMCP Agent Companion.app");
        var agentPlist = Path.Combine(home, "Library", "LaunchAgents", "com.matemcp.agent.plist");
        return $$"""
#!/bin/sh
set -u
TEMP_ROOT={{ShellQuote(tempRoot)}}
ARCHIVE={{ShellQuote(archivePath)}}
PACKAGE="$TEMP_ROOT/package"
MARKER={{ShellQuote(markerPath)}}
FAILURE={{ShellQuote(failurePath)}}
LOG={{ShellQuote(logPath)}}
COMPANION={{ShellQuote(companionPath)}}
AGENT_PLIST={{ShellQuote(agentPlist)}}
LAUNCH_DOMAIN="gui/$(id -u)"
sleep 2
mkdir -p "$PACKAGE" "$(dirname "$MARKER")" "$(dirname "$LOG")"
rm -f "$FAILURE"
if tar -xzf "$ARCHIVE" -C "$PACKAGE" >>"$LOG" 2>&1 && \
   test -f "$PACKAGE/install-desktop-macos.sh" && \
   chmod +x "$PACKAGE/install-desktop-macos.sh" && \
   "$PACKAGE/install-desktop-macos.sh" --no-start >>"$LOG" 2>&1; then
    printf '%s' '{{assetId.ToString(CultureInfo.InvariantCulture)}}' > "$MARKER"
    rm -f "$FAILURE"
    launchctl bootstrap "$LAUNCH_DOMAIN" "$AGENT_PLIST" >>"$LOG" 2>&1 || true
    launchctl kickstart -k "$LAUNCH_DOMAIN/com.matemcp.agent" >>"$LOG" 2>&1 || \
        printf '%s\n' "Desktop updated, but the Agent could not be restarted automatically. See $LOG for details." > "$FAILURE"
    open "$COMPANION" >>"$LOG" 2>&1 || true
    rm -rf "$TEMP_ROOT"
    exit 0
else
    code=$?
    printf '%s\n' "Desktop update installation failed. See $LOG for details." > "$FAILURE"
    launchctl bootstrap "$LAUNCH_DOMAIN" "$AGENT_PLIST" >>"$LOG" 2>&1 || true
    launchctl kickstart -k "$LAUNCH_DOMAIN/com.matemcp.agent" >>"$LOG" 2>&1 || true
    open "$COMPANION" >>"$LOG" 2>&1 || true
    rm -rf "$TEMP_ROOT"
    exit "$code"
fi
""";
    }

    private static string BuildWindowsInstallScript(string tempRoot, string archivePath, string markerPath, string failurePath, long assetId)
    {
        return $$"""
$ErrorActionPreference = 'Stop'
$TempRoot = {{PowerShellQuote(tempRoot)}}
$Archive = {{PowerShellQuote(archivePath)}}
$Package = Join-Path $TempRoot 'package'
$Marker = {{PowerShellQuote(markerPath)}}
$Failure = {{PowerShellQuote(failurePath)}}
$LogRoot = Join-Path $env:LOCALAPPDATA 'MateMCP-Update'
$Log = Join-Path $LogRoot 'update.log'
$InstalledRoot = Join-Path $env:LOCALAPPDATA 'MateMCP'
$Companion = Join-Path $InstalledRoot 'Companion\MateMCP.Agent.Companion.exe'
$HiddenLauncher = Join-Path $InstalledRoot 'start-agent-hidden.vbs'
$WScript = Join-Path $env:WINDIR 'System32\wscript.exe'
Start-Sleep -Seconds 2
New-Item -ItemType Directory -Force -Path $Package, $LogRoot, (Split-Path $Marker) | Out-Null
Remove-Item $Failure -Force -ErrorAction SilentlyContinue
try {
    Expand-Archive -Path $Archive -DestinationPath $Package -Force
    $Installer = Join-Path $Package 'install-desktop-windows.ps1'
    if (-not (Test-Path $Installer)) { throw 'Downloaded package does not contain install-desktop-windows.ps1' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Installer -NoStart *> $Log
    if ($LASTEXITCODE -ne 0) { throw "MateMCP Desktop installer exited with code $LASTEXITCODE" }
    New-Item -ItemType Directory -Force -Path (Split-Path $Marker) | Out-Null
    [IO.File]::WriteAllText($Marker, '{{assetId.ToString(CultureInfo.InvariantCulture)}}')
    Remove-Item $Failure -Force -ErrorAction SilentlyContinue
    if (Test-Path $HiddenLauncher) { Start-Process -FilePath $WScript -ArgumentList "`"$HiddenLauncher`"" }
    Start-Sleep -Milliseconds 750
    if (Test-Path $Companion) { Start-Process -FilePath $Companion -WorkingDirectory (Split-Path $Companion) }
}
catch {
    New-Item -ItemType Directory -Force -Path (Split-Path $Failure) | Out-Null
    [IO.File]::WriteAllText($Failure, "Desktop update installation failed: $($_.Exception.Message). See $Log for details.")
    if (Test-Path $HiddenLauncher) { Start-Process -FilePath $WScript -ArgumentList "`"$HiddenLauncher`"" -ErrorAction SilentlyContinue }
    if (Test-Path $Companion) { Start-Process -FilePath $Companion -WorkingDirectory (Split-Path $Companion) -ErrorAction SilentlyContinue }
}
finally {
    Remove-Item $TempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
""";
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string PowerShellQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string? ReadLastFailure()
    {
        try
        {
            var path = GetFailurePath();
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
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

    private static string GetMarkerPath() => Path.Combine(GetStateDirectory(), MarkerFileName);
    private static string GetFailurePath() => Path.Combine(GetStateDirectory(), FailureFileName);

    private static string GetStateDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MateMCP");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, "Library", "Application Support", "MateMCP");
    }

    private static string? GetAssetName()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return "MateMCP-Desktop-win-x64.zip";
        if ((OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS()) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return "MateMCP-Desktop-macos-arm64.tar.gz";
        return null;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort cleanup only. The OS temp directory can reclaim leftovers.
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record GitHubRelease([property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets);
    private sealed record GitHubAsset(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("size")] long Size);
}

public sealed record DesktopUpdateStatus(
    bool Supported,
    bool UpdateAvailable,
    long? AssetId,
    DateTimeOffset? UpdatedAt,
    string Message,
    string? LastFailure = null);

public sealed record DesktopUpdateProgress(string Stage, string Message, long BytesReceived, long? TotalBytes)
{
    public int? Percentage => TotalBytes is > 0
        ? (int)Math.Clamp(BytesReceived * 100 / TotalBytes.Value, 0, 100)
        : null;
}
