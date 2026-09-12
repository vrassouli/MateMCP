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

/// <summary>
/// Tracks a privacy-visible local Computer Use session independently from short-lived MCP request leases.
/// A local Stop is represented by a user-owned sentinel beside the Agent configuration, so Companion can
/// revoke control even when no management request is currently in flight. Every visual/input action checks
/// the sentinel before touching the desktop.
/// </summary>
public sealed class ComputerUseSessionManager
{
    public const string StatusFileName = "computer-use-status.json";
    public const string StopFileName = "computer-use.stop";
    public static ComputerUseSessionManager Shared { get; } = new();
    public static readonly TimeSpan ActiveIdleWindow = TimeSpan.FromSeconds(45);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _dataDirectory;
    private readonly string _statusPath;
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
        _stopPath = Path.Combine(_dataDirectory, StopFileName);
    }

    public ComputerUseStatus GetStatus()
    {
        lock (_sync)
        {
            SyncExternalStop_NoLock();
            var status = Snapshot(DateTimeOffset.UtcNow);
            Persist_NoLock(status);
            return status;
        }
    }

    public void EnsureAvailable()
    {
        lock (_sync)
        {
            SyncExternalStop_NoLock();
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
            SyncExternalStop_NoLock();
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
            try { if (File.Exists(_stopPath)) File.Delete(_stopPath); } catch (IOException) { }
            _blocked = false;
            _sessionId = null;
            _mode = null;
            _target = null;
            _startedAt = null;
            _lastActivityAt = null;
            _revokedAt = null;
            _revokeReason = null;
            var status = Snapshot(DateTimeOffset.UtcNow);
            Persist_NoLock(status);
            return status;
        }
    }

    private void SyncExternalStop_NoLock()
    {
        if (!File.Exists(_stopPath)) return;
        if (_blocked) return;
        _blocked = true;
        _revokedAt = DateTimeOffset.UtcNow;
        _revokeReason = "Stopped from local Companion.";
        _lastActivityAt = null;
        Persist_NoLock(Snapshot(DateTimeOffset.UtcNow));
    }

    private void Persist_NoLock(ComputerUseStatus status)
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            var temp = _statusPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(status, Json) + Environment.NewLine);
            ConfigurationBootstrap.TryRestrictPermissions(temp);
            File.Move(temp, _statusPath, overwrite: true);
            ConfigurationBootstrap.TryRestrictPermissions(_statusPath);
        }
        catch (IOException)
        {
            // Session safety remains enforced in-memory and through the stop sentinel even if status persistence fails.
        }
        catch (UnauthorizedAccessException)
        {
            // Companion status visibility is best-effort; do not disable the safety gate if the status file cannot be written.
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
