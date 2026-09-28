using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MateMCP.Agent.Companion.Services;

public sealed record CompanionUnexpectedExitInfo(
    string SessionId,
    DateTimeOffset LastEventAt,
    string LastEvent,
    int ProcessId);

public sealed record CompanionLifecycleEntry(
    DateTimeOffset Timestamp,
    string SessionId,
    string Event,
    string? Detail,
    int ProcessId,
    string Platform,
    string Version);

public sealed class CompanionLifecycleStore
{
    private const long DefaultMaxBytes = 256 * 1024;
    private const int TrimTargetLines = 128;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> CleanTerminalEvents = new(StringComparer.Ordinal)
    {
        "window-closed",
        "process-exit",
        "update-handoff"
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly Func<int, bool> _isProcessAlive;
    private string? _sessionId;
    private bool _terminalRecorded;

    public CompanionLifecycleStore()
        : this(GetDefaultPath())
    {
    }

    public CompanionLifecycleStore(
        string path,
        long maxBytes = DefaultMaxBytes,
        Func<int, bool>? isProcessAlive = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        _path = path;
        _maxBytes = maxBytes;
        _isProcessAlive = isProcessAlive ?? IsProcessAlive;
    }

    public string LogPath => _path;
    public CompanionUnexpectedExitInfo? PreviousUnexpectedExit { get; private set; }

    public void BeginSession()
    {
        lock (_gate)
        {
            if (_sessionId is not null) return;

            var previous = ReadLastEntryUnsafe();
            if (previous is not null
                && !CleanTerminalEvents.Contains(previous.Event)
                && !_isProcessAlive(previous.ProcessId))
            {
                PreviousUnexpectedExit = new(
                    previous.SessionId,
                    previous.Timestamp,
                    previous.Event,
                    previous.ProcessId);

                AppendUnsafe(new CompanionLifecycleEntry(
                    DateTimeOffset.UtcNow,
                    previous.SessionId,
                    "unexpected-exit-detected",
                    $"last-event={Limit(previous.Event)}",
                    Environment.ProcessId,
                    GetPlatform(),
                    GetVersion()));
            }

            _sessionId = Guid.NewGuid().ToString("N");
            _terminalRecorded = false;
            AppendUnsafe(CreateCurrentEntry("started", null));
        }
    }

    public void Record(string eventName, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        lock (_gate)
        {
            EnsureSessionUnsafe();
            if (_terminalRecorded) return;
            AppendUnsafe(CreateCurrentEntry(eventName, Limit(detail)));
        }
    }

    public void RecordUnhandled(Exception? exception, bool terminating)
    {
        var detail = $"terminating={terminating};type={Limit(exception?.GetType().FullName ?? "unknown")}";

        lock (_gate)
        {
            EnsureSessionUnsafe();
            if (_terminalRecorded) return;

            if (terminating)
            {
                _terminalRecorded = true;
                AppendUnsafe(CreateCurrentEntry("unhandled-exception", detail));
            }
            else
            {
                AppendUnsafe(CreateCurrentEntry("unobserved-task-exception", detail));
            }
        }
    }

    public void MarkTerminal(string reason, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_gate)
        {
            EnsureSessionUnsafe();
            if (_terminalRecorded) return;

            _terminalRecorded = true;
            AppendUnsafe(CreateCurrentEntry(reason, Limit(detail)));
        }
    }

    public IReadOnlyList<CompanionLifecycleEntry> ReadRecent(int limit = 100)
    {
        if (limit <= 0) return [];

        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return [];

                var entries = new List<CompanionLifecycleEntry>();
                foreach (var line in File.ReadLines(_path))
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<CompanionLifecycleEntry>(line, Json);
                        if (entry is not null) entries.Add(entry);
                    }
                    catch (JsonException)
                    {
                    }
                }

                return entries.TakeLast(limit).ToArray();
            }
            catch (IOException)
            {
                return [];
            }
            catch (UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    private void EnsureSessionUnsafe()
    {
        if (_sessionId is not null) return;
        _sessionId = Guid.NewGuid().ToString("N");
        _terminalRecorded = false;
        AppendUnsafe(CreateCurrentEntry("started", "late-initialization"));
    }

    private CompanionLifecycleEntry CreateCurrentEntry(string eventName, string? detail)
        => new(
            DateTimeOffset.UtcNow,
            _sessionId!,
            eventName,
            detail,
            Environment.ProcessId,
            GetPlatform(),
            GetVersion());

    private CompanionLifecycleEntry? ReadLastEntryUnsafe()
    {
        try
        {
            if (!File.Exists(_path)) return null;

            CompanionLifecycleEntry? last = null;
            foreach (var line in File.ReadLines(_path))
            {
                try
                {
                    last = JsonSerializer.Deserialize<CompanionLifecycleEntry>(line, Json) ?? last;
                }
                catch (JsonException)
                {
                }
            }

            return last;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void AppendUnsafe(CompanionLifecycleEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var line = JsonSerializer.Serialize(entry, Json) + Environment.NewLine;
            File.AppendAllText(_path, line, Encoding.UTF8);
            TrimIfNeededUnsafe();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void TrimIfNeededUnsafe()
    {
        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists || info.Length <= _maxBytes) return;

            var lines = File.ReadAllLines(_path);
            var kept = new List<string>();
            var budget = Math.Max(1024, _maxBytes / 2);
            var bytes = 0;

            for (var i = lines.Length - 1; i >= 0 && kept.Count < TrimTargetLines; i--)
            {
                var lineBytes = Encoding.UTF8.GetByteCount(lines[i]) + Environment.NewLine.Length;
                if (kept.Count > 0 && bytes + lineBytes > budget) break;
                kept.Add(lines[i]);
                bytes += lineBytes;
            }

            kept.Reverse();
            File.WriteAllLines(_path, kept, Encoding.UTF8);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string? Limit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        const int max = 240;
        return value.Length <= max ? value : value[..max];
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string GetVersion()
        => typeof(CompanionLifecycleStore).Assembly.GetName().Version?.ToString() ?? "unknown";

    private static string GetPlatform()
        => OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS() ? "macos"
            : "other";

    private static string GetDefaultPath()
    {
        string root;
        if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Application Support",
                "MateMCP");
        }
        else
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MateMCP-Companion");
        }

        return Path.Combine(root, "companion-lifecycle.jsonl");
    }
}
