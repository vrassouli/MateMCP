using System.Text.Json;
using MateMCP.Agent.Configuration;

namespace MateMCP.Agent.Desktop;

public sealed record ComputerUseStatus(
    bool Active,
    bool Blocked,
    string? SessionId,
    string? Mode,
    string? Target,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset? RevokedAt,
    string? RevokeReason);

public sealed record ComputerUseIndicator(
    bool Blocked,
    string? Mode,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastActivityAt,
    DateTimeOffset? RevokedAt);

/// <summary>
/// Tracks a privacy-visible local Computer Use session independently from short-lived MCP request leases.
/// A local Stop is represented by a sentinel beside the Agent configuration, so Companion can revoke control
/// without relying on Relay or on an in-flight MCP request. Every visual/input action checks the sentinel.
/// </summary>
public sealed class ComputerUseSessionManager
{
    public const string StatusFileName = "computer-use-status.json";
    public const string IndicatorFileName = "computer-use-indicator.json";
    public const string StopFileName = "computer-use.stop";
    public static ComputerUseSessionManager Shared { get; } = new();
    public static readonly TimeSpan ActiveIdleWindow = TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _dataDirectory;
    private readonly string _statusPath;
    private readonly string _indicatorPath;
    private readonly string _stopPath;
    private string? _sessionId;
    private string? _mode;
    private string? _target;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _lastActivityAt;
    private bool _blocked;
    private DateTimeOffset? _revokedAt;
    private string? _revokeReason;

    public ComputerUseSessionManager(string? dataDirectory = null)
    {
        _dataDirectory = string.IsNullOrWhiteSpace(dataDirectory)
            ? ConfigurationBootstrap.GetUserDataDirectory()
            : Path.GetFullPath(dataDirectory);
        _statusPath = Path.Combine(_dataDirectory, StatusFileName);
        _indicatorPath = Path.Combine(_dataDirectory, IndicatorFileName);
        _stopPath = Path.Combine(_dataDirectory, StopFileName);
    }

    public ComputerUseStatus GetStatus()
    {
        lock (_sync)
        {
            SyncExternalControl_NoLock();
            var status = Snapshot(DateTimeOffset.UtcNow);
            Persist_NoLock(status);
            return status;
        }
    }

    public void EnsureAvailable()
    {
        lock (_sync)
        {
            SyncExternalControl_NoLock();
            if (_blocked)
                throw new InvalidOperationException("Computer Use was stopped by the local user. Resume it locally before issuing more visual or input actions.");
        }
    }

    public ComputerUseStatus Touch(string mode, string? target = null)
    {
        if (string.IsNullOrWhiteSpace(mode)) throw new ArgumentException("Computer Use mode is required.", nameof(mode));
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            SyncExternalControl_NoLock();
            if (_blocked)
                throw new InvalidOperationException("Computer Use was stopped by the local user. Resume it locally before issuing more visual or input actions.");

            var wasActive = IsActive(now);
            if (!wasActive)
            {
                _sessionId = Guid.NewGuid().ToString("n");
                _startedAt = now;
            }

            _mode = mode.Trim();
            _target = string.IsNullOrWhiteSpace(target) ? null : Trim(target, 160);
            _lastActivityAt = now;
            var status = Snapshot(now);
            Persist_NoLock(status);
            return status;
        }
    }

    public ComputerUseStatus Stop(string reason = "Stopped by local user")
    {
        var now = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            Directory.CreateDirectory(_dataDirectory);
            File.WriteAllText(_stopPath, now.ToString("O") + Environment.NewLine);
            ConfigurationBootstrap.TryRestrictPermissions(_stopPath);
            _blocked = true;
            _revokedAt = now;
            _revokeReason = Trim(string.IsNullOrWhiteSpace(reason) ? "Stopped by local user" : reason, 240);
            _lastActivityAt = null;
            var status = Snapshot(now);
            Persist_NoLock(status);
            return status;
        }
    }

    public ComputerUseStatus Resume()
    {
        lock (_sync)
        {
            DeleteStopSentinel_NoThrow();
            ResetBlock_NoLock();
            var status = Snapshot(DateTimeOffset.UtcNow);
            Persist_NoLock(status);
            return status;
        }
    }

    private void SyncExternalControl_NoLock()
    {
        if (File.Exists(_stopPath))
        {
            if (_blocked) return;
            _blocked = true;
            _revokedAt = DateTimeOffset.UtcNow;
            _revokeReason = "Stopped from local Companion.";
            _lastActivityAt = null;
            Persist_NoLock(Snapshot(DateTimeOffset.UtcNow));
            return;
        }

        // Removing the local stop sentinel is the explicit local Resume action. Never restore the old session.
        if (_blocked)
        {
            ResetBlock_NoLock();
            Persist_NoLock(Snapshot(DateTimeOffset.UtcNow));
        }
    }

    private void ResetBlock_NoLock()
    {
        _blocked = false;
        _sessionId = null;
        _mode = null;
        _target = null;
        _startedAt = null;
        _lastActivityAt = null;
        _revokedAt = null;
        _revokeReason = null;
    }

    private void DeleteStopSentinel_NoThrow()
    {
        try { if (File.Exists(_stopPath)) File.Delete(_stopPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Persist_NoLock(ComputerUseStatus status)
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            WriteAtomic(_statusPath, JsonSerializer.Serialize(status, Json), privateFile: true);

            // The Companion only needs a non-sensitive indicator. Do not expose session id, target/window title,
            // or revoke reason here; the full status stays private to the Agent user/process.
            var indicator = new ComputerUseIndicator(
                status.Blocked,
                status.Mode,
                status.StartedAt,
                status.LastActivityAt,
                status.RevokedAt);
            WriteAtomic(_indicatorPath, JsonSerializer.Serialize(indicator, Json), privateFile: false);
        }
        catch (IOException)
        {
            // Session safety remains enforced in-memory and through the stop sentinel even if persistence fails.
        }
        catch (UnauthorizedAccessException)
        {
            // Indicator/status visibility is best-effort; do not disable the safety gate if files cannot be written.
        }
    }

    private static void WriteAtomic(string path, string content, bool privateFile)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content + Environment.NewLine);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            try
            {
                File.SetUnixFileMode(temp, privateFile
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            catch { }
        }
        File.Move(temp, path, overwrite: true);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            try
            {
                File.SetUnixFileMode(path, privateFile
                    ? UnixFileMode.UserRead | UnixFileMode.UserWrite
                    : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            catch { }
        }
    }

    private ComputerUseStatus Snapshot(DateTimeOffset now)
    {
        var active = IsActive(now);
        return new ComputerUseStatus(
            Active: active,
            Blocked: _blocked,
            SessionId: active ? _sessionId : null,
            Mode: active ? _mode : null,
            Target: active ? _target : null,
            StartedAt: active ? _startedAt : null,
            LastActivityAt: active ? _lastActivityAt : null,
            RevokedAt: _revokedAt,
            RevokeReason: _revokeReason);
    }

    private bool IsActive(DateTimeOffset now)
        => !_blocked && _lastActivityAt is { } last && now - last <= ActiveIdleWindow;

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
