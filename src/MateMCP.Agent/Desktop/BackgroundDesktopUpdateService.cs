using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using MateMCP.Agent.Security;
using MateMCP.Agent.Tools;

namespace MateMCP.Agent.Desktop;

public sealed record BackgroundDesktopUpdateStatus(
    bool AutoUpdateEnabled,
    string State,
    string Message,
    DateTimeOffset? LastChangedAt,
    long InstalledAssetId,
    string? LastFailure);

public sealed class BackgroundDesktopUpdateService(
    IHttpClientFactory clients,
    DesktopUpdateSettingsStore settings,
    AgentActivityGate activity,
    InteractiveShellSessionManager sessions,
    ApprovalService approvals,
    IHostApplicationLifetime lifetime,
    ILogger<BackgroundDesktopUpdateService> logger) : BackgroundService
{
    private const string ReleaseApi = "https://api.github.com/repos/vrassouli/MateMCP/releases/tags/agent-latest";
    private const string MarkerFileName = ".desktop-background-release-asset";
    private const string StatusFileName = ".desktop-background-update-status";
    private const string FailureFileName = ".desktop-background-update-error";
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void RequestCheck()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    public async Task<BackgroundDesktopUpdateStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var currentSettings = await settings.GetAsync(ct);
        var (state, message, changedAt) = ReadStatus();
        return new BackgroundDesktopUpdateStatus(
            currentSettings.AutoUpdateEnabled,
            state,
            message,
            changedAt,
            ReadInstalledAssetId(),
            ReadText(GetFailurePath()));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if ((await settings.GetAsync(stoppingToken)).AutoUpdateEnabled)
                        await CheckAndInstallAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Background Desktop update check failed.");
                    WriteFailure($"Background Desktop update failed: {ex.Message}");
                    WriteStatus("failed", $"Background update check failed: {ex.Message}");
                }

                var delay = Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
                var wake = _wake.WaitAsync(stoppingToken);
                await Task.WhenAny(delay, wake);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task CheckAndInstallAsync(CancellationToken ct)
    {
        var assetName = GetAssetName();
        if (assetName is null)
        {
            WriteStatus("unsupported", "Background Desktop updates are not available for this architecture.");
            return;
        }

        if (IsBusy())
        {
            WriteStatus("deferred", "Update deferred because Agent work or an approval is active.");
            return;
        }

        WriteStatus("checking", "Checking the MateMCP Desktop release in the background.");
        var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MateMCP-Agent/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var release = await client.GetFromJsonAsync<GitHubRelease>(ReleaseApi, ct)
            ?? throw new InvalidOperationException("Could not read Desktop release metadata.");
        var asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.OrdinalIgnoreCase));
        if (asset is null)
        {
            WriteStatus("unavailable", $"Release asset {assetName} is not available.");
            return;
        }

        var installedAssetId = ReadInstalledAssetId();
        if (installedAssetId == 0)
        {
            WriteInstalledAssetId(asset.Id);
            ClearFailure();
            WriteStatus("current", "Background updater baseline established from the installed Desktop package.");
            return;
        }

        if (installedAssetId == asset.Id)
        {
            ClearFailure();
            WriteStatus("current", "MateMCP Desktop is up to date.");
            return;
        }

        var expectedHash = ParseSha256Digest(asset.Digest);
        var tempRoot = Path.Combine(Path.GetTempPath(), "matemcp-background-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var archivePath = Path.Combine(tempRoot, asset.Name);

        try
        {
            WriteStatus("downloading", "Downloading a verified Desktop update in the background.");
            await DownloadAndVerifyAsync(client, asset, archivePath, expectedHash, ct);

            if (IsBusy())
            {
                WriteStatus("deferred", "Verified update downloaded, but installation was deferred because Agent work became active.");
                TryDeleteDirectory(tempRoot);
                return;
            }

            if (!activity.TryBeginDrain())
            {
                WriteStatus("deferred", "Verified update downloaded, but installation was deferred because Agent work became active.");
                TryDeleteDirectory(tempRoot);
                return;
            }

            if (sessions.ActiveSessionCount != 0 || approvals.GetPending().Count != 0)
            {
                activity.CancelDrain();
                WriteStatus("deferred", "Verified update downloaded, but installation was deferred because a shell session or approval is active.");
                TryDeleteDirectory(tempRoot);
                return;
            }

            try
            {
                ClearFailure();
                WriteStatus("installing", "Verified Desktop update is ready; restarting the Agent to install it.");
                LaunchInstaller(tempRoot, archivePath, asset.Id);
                lifetime.StopApplication();
            }
            catch
            {
                activity.CancelDrain();
                throw;
            }
        }
        catch
        {
            TryDeleteDirectory(tempRoot);
            throw;
        }
    }

    private bool IsBusy()
        => activity.ActiveCount != 0 || activity.IsDraining || sessions.ActiveSessionCount != 0 || approvals.GetPending().Count != 0;

    private static async Task DownloadAndVerifyAsync(HttpClient client, GitHubAsset asset, string archivePath, byte[] expectedHash, CancellationToken ct)
    {
        using var response = await client.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var expectedSize = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : null);
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[81920];
        long received = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0) break;
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            hash.AppendData(buffer, 0, read);
            received += read;
        }
        await target.FlushAsync(ct);

        if (expectedSize is > 0 && received != expectedSize)
            throw new IOException($"Desktop update download was incomplete ({received} of {expectedSize} bytes received).");

        var actualHash = hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            throw new InvalidDataException("Desktop update SHA-256 verification failed. Installation was aborted.");
    }

    private static byte[] ParseSha256Digest(string? digest)
    {
        const string prefix = "sha256:";
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Desktop release asset does not include a supported SHA-256 digest. Automatic installation was aborted.");

        var hex = digest[prefix.Length..].Trim();
        try
        {
            var bytes = Convert.FromHexString(hex);
            return bytes.Length == 32 ? bytes : throw new FormatException();
        }
        catch (FormatException)
        {
            throw new InvalidDataException("Desktop release asset contains an invalid SHA-256 digest.");
        }
    }

    private static void LaunchInstaller(string tempRoot, string archivePath, long assetId)
    {
        Directory.CreateDirectory(GetStateDirectory());
        var marker = GetMarkerPath();
        var status = GetStatusPath();
        var failure = GetFailurePath();

        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(tempRoot, "install-background-update.ps1");
            File.WriteAllText(scriptPath, BuildWindowsInstallScript(tempRoot, archivePath, marker, status, failure, assetId));
            var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\""
            });
            if (process is null) throw new InvalidOperationException("Could not start the Windows background update installer.");
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            var scriptPath = Path.Combine(tempRoot, "install-background-update.sh");
            File.WriteAllText(scriptPath, BuildMacInstallScript(tempRoot, archivePath, marker, status, failure, assetId));
            try { File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
            var process = Process.Start(new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { scriptPath }
            });
            if (process is null) throw new InvalidOperationException("Could not start the macOS background update installer.");
            return;
        }

        throw new PlatformNotSupportedException("Background Desktop self-update is supported on Windows and macOS.");
    }

    private static string BuildWindowsInstallScript(string tempRoot, string archivePath, string markerPath, string statusPath, string failurePath, long assetId)
    {
        return $$"""
$ErrorActionPreference = 'Stop'
$TempRoot = {{PowerShellQuote(tempRoot)}}
$Archive = {{PowerShellQuote(archivePath)}}
$Package = Join-Path $TempRoot 'package'
$Marker = {{PowerShellQuote(markerPath)}}
$Status = {{PowerShellQuote(statusPath)}}
$Failure = {{PowerShellQuote(failurePath)}}
$InstalledRoot = Join-Path $env:LOCALAPPDATA 'MateMCP'
$HiddenLauncher = Join-Path $InstalledRoot 'start-agent-hidden.vbs'
$WScript = Join-Path $env:WINDIR 'System32\wscript.exe'
Start-Sleep -Seconds 2
New-Item -ItemType Directory -Force -Path $Package, (Split-Path $Marker) | Out-Null
try {
    Expand-Archive -Path $Archive -DestinationPath $Package -Force
    $Installer = Join-Path $Package 'install-desktop-windows.ps1'
    if (-not (Test-Path $Installer)) { throw 'Downloaded package does not contain install-desktop-windows.ps1' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Installer -NoStart
    if ($LASTEXITCODE -ne 0) { throw "MateMCP Desktop installer exited with code $LASTEXITCODE" }
    [IO.File]::WriteAllText($Marker, '{{assetId.ToString(CultureInfo.InvariantCulture)}}')
    [IO.File]::WriteAllText($Status, "$(Get-Date -AsUTC -Format o)|updated|MateMCP Desktop was updated automatically in the background.")
    Remove-Item $Failure -Force -ErrorAction SilentlyContinue
    if (Test-Path $HiddenLauncher) { Start-Process -FilePath $WScript -ArgumentList "`"$HiddenLauncher`"" }
}
catch {
    New-Item -ItemType Directory -Force -Path (Split-Path $Failure) | Out-Null
    [IO.File]::WriteAllText($Failure, "Background Desktop update installation failed: $($_.Exception.Message)")
    [IO.File]::WriteAllText($Status, "$(Get-Date -AsUTC -Format o)|failed|Background Desktop update installation failed; the previous installation will be restarted if possible.")
    if (Test-Path $HiddenLauncher) { Start-Process -FilePath $WScript -ArgumentList "`"$HiddenLauncher`"" -ErrorAction SilentlyContinue }
}
finally {
    Remove-Item $TempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
""";
    }

    private static string BuildMacInstallScript(string tempRoot, string archivePath, string markerPath, string statusPath, string failurePath, long assetId)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var agentPlist = Path.Combine(home, "Library", "LaunchAgents", "com.matemcp.agent.plist");
        return $$"""
#!/bin/sh
set -u
TEMP_ROOT={{ShellQuote(tempRoot)}}
ARCHIVE={{ShellQuote(archivePath)}}
PACKAGE="$TEMP_ROOT/package"
MARKER={{ShellQuote(markerPath)}}
STATUS={{ShellQuote(statusPath)}}
FAILURE={{ShellQuote(failurePath)}}
AGENT_PLIST={{ShellQuote(agentPlist)}}
LAUNCH_DOMAIN="gui/$(id -u)"
sleep 2
mkdir -p "$PACKAGE" "$(dirname "$MARKER")"
if tar -xzf "$ARCHIVE" -C "$PACKAGE" && \
   test -f "$PACKAGE/install-desktop-macos.sh" && \
   chmod +x "$PACKAGE/install-desktop-macos.sh" && \
   "$PACKAGE/install-desktop-macos.sh" --no-start; then
    printf '%s' '{{assetId.ToString(CultureInfo.InvariantCulture)}}' > "$MARKER"
    printf '%s|updated|%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" 'MateMCP Desktop was updated automatically in the background.' > "$STATUS"
    rm -f "$FAILURE"
    launchctl bootstrap "$LAUNCH_DOMAIN" "$AGENT_PLIST" >/dev/null 2>&1 || true
    launchctl kickstart -k "$LAUNCH_DOMAIN/com.matemcp.agent" >/dev/null 2>&1 || true
else
    code=$?
    printf '%s\n' 'Background Desktop update installation failed; the previous installation will be restarted if possible.' > "$FAILURE"
    printf '%s|failed|%s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" 'Background Desktop update installation failed; the previous installation will be restarted if possible.' > "$STATUS"
    launchctl bootstrap "$LAUNCH_DOMAIN" "$AGENT_PLIST" >/dev/null 2>&1 || true
    launchctl kickstart -k "$LAUNCH_DOMAIN/com.matemcp.agent" >/dev/null 2>&1 || true
    rm -rf "$TEMP_ROOT"
    exit "$code"
fi
rm -rf "$TEMP_ROOT"
""";
    }

    private static (string State, string Message, DateTimeOffset? ChangedAt) ReadStatus()
    {
        var text = ReadText(GetStatusPath());
        if (string.IsNullOrWhiteSpace(text)) return ("idle", "No background update activity has been recorded yet.", null);
        var parts = text.Split('|', 3);
        if (parts.Length != 3) return ("unknown", text, null);
        return (parts[1], parts[2], DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var changed) ? changed : null);
    }

    private static void WriteStatus(string state, string message)
    {
        try
        {
            Directory.CreateDirectory(GetStateDirectory());
            var singleLine = message.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
            File.WriteAllText(GetStatusPath(), $"{DateTimeOffset.UtcNow:o}|{state}|{singleLine}");
            TryRestrict(GetStatusPath());
        }
        catch
        {
        }
    }

    private static void WriteFailure(string message)
    {
        try
        {
            Directory.CreateDirectory(GetStateDirectory());
            File.WriteAllText(GetFailurePath(), message);
            TryRestrict(GetFailurePath());
        }
        catch
        {
        }
    }

    private static void ClearFailure()
    {
        try { File.Delete(GetFailurePath()); } catch { }
    }

    private static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch { return null; }
    }

    private static long ReadInstalledAssetId()
    {
        var value = ReadText(GetMarkerPath());
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0;
    }

    private static void WriteInstalledAssetId(long assetId)
    {
        Directory.CreateDirectory(GetStateDirectory());
        File.WriteAllText(GetMarkerPath(), assetId.ToString(CultureInfo.InvariantCulture));
        TryRestrict(GetMarkerPath());
    }

    private static string GetStateDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MateMCP");
    private static string GetMarkerPath() => Path.Combine(GetStateDirectory(), MarkerFileName);
    private static string GetStatusPath() => Path.Combine(GetStateDirectory(), StatusFileName);
    private static string GetFailurePath() => Path.Combine(GetStateDirectory(), FailureFileName);

    private static string? GetAssetName()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return "MateMCP-Desktop-win-x64.zip";
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return "MateMCP-Desktop-macos-arm64.tar.gz";
        return null;
    }

    private static string PowerShellQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }

    private static void TryRestrict(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    }

    private sealed record GitHubRelease([property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets);
    private sealed record GitHubAsset(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest);
}
