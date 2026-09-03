using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MateMCP.Agent.Diagnostics;

public sealed record AgentLogEntry(long Id, DateTimeOffset Timestamp, LogLevel Level, string Category, string Message);

public sealed class AgentLogStore
{
    private readonly object _gate = new();
    private readonly LinkedList<AgentLogEntry> _entries = new();
    private readonly string _path;
    private readonly int _capacity;
    private readonly long _maxFileBytes;
    private long _nextId;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public AgentLogStore(string path, int capacity = 1000, long maxFileBytes = 2 * 1024 * 1024)
    {
        if (capacity < 10) throw new ArgumentOutOfRangeException(nameof(capacity));
        _path = path;
        _capacity = capacity;
        _maxFileBytes = Math.Max(64 * 1024, maxFileBytes);
        LoadRecent();
    }

    public void Append(LogLevel level, string category, string message)
    {
        if (level == LogLevel.None || string.IsNullOrWhiteSpace(message)) return;
        var entry = new AgentLogEntry(Interlocked.Increment(ref _nextId), DateTimeOffset.UtcNow, level,
            string.IsNullOrWhiteSpace(category) ? "General" : category, AgentLogRedactor.Redact(message));

        lock (_gate)
        {
            _entries.AddLast(entry);
            while (_entries.Count > _capacity) _entries.RemoveFirst();
            PersistAppend(entry);
        }
    }

    public IReadOnlyList<AgentLogEntry> Read(long afterId = 0, int limit = 500, LogLevel? minimumLevel = null, string? text = null)
    {
        limit = Math.Clamp(limit, 1, _capacity);
        lock (_gate)
        {
            IEnumerable<AgentLogEntry> query = _entries.Where(x => x.Id > afterId);
            if (minimumLevel is not null) query = query.Where(x => x.Level >= minimumLevel.Value);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var q = text.Trim();
                query = query.Where(x => x.Message.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || x.Category.Contains(q, StringComparison.OrdinalIgnoreCase));
            }
            return query.TakeLast(limit).ToArray();
        }
    }

    public long LatestId
    {
        get { lock (_gate) return _entries.Last?.Value.Id ?? _nextId; }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, string.Empty);
        }
    }

    private void LoadRecent()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return;
                var loaded = new Queue<AgentLogEntry>(_capacity);
                foreach (var line in File.ReadLines(_path))
                {
                    try
                    {
                        var item = JsonSerializer.Deserialize<AgentLogEntry>(line, Json);
                        if (item is null) continue;
                        loaded.Enqueue(item);
                        while (loaded.Count > _capacity) loaded.Dequeue();
                        _nextId = Math.Max(_nextId, item.Id);
                    }
                    catch (JsonException) { }
                }
                foreach (var item in loaded) _entries.AddLast(item);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void PersistAppend(AgentLogEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
            if (new FileInfo(_path).Length > _maxFileBytes) RewriteBoundedFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void RewriteBoundedFile()
    {
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var writer = new StreamWriter(temp, false, new System.Text.UTF8Encoding(false)))
                foreach (var item in _entries) writer.WriteLine(JsonSerializer.Serialize(item, Json));
            File.Move(temp, _path, overwrite: true);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
}

public sealed partial class AgentLogRedactor
{
    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*)(?:bearer\\s+)?[^\\s,;]+")]
    private static partial Regex AuthorizationPattern();
    [GeneratedRegex("(?i)\\b(password|passwd|token|access[_-]?token|refresh[_-]?token|api[_-]?key|client[_-]?secret|secret)\\b(\\s*[:=]\\s*)[^\\s,;]+")]
    private static partial Regex KeyValuePattern();
    [GeneratedRegex("(?i)\\bbearer\\s+[A-Za-z0-9._~+/-]+=*")]
    private static partial Regex BearerPattern();

    public static string Redact(string value)
    {
        var redacted = AuthorizationPattern().Replace(value, "$1[REDACTED]");
        redacted = KeyValuePattern().Replace(redacted, "$1$2[REDACTED]");
        return BearerPattern().Replace(redacted, "Bearer [REDACTED]");
    }
}

public sealed class AgentLogProvider(AgentLogStore store) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new StoreLogger(store, categoryName);
    public void Dispose() { }

    private sealed class StoreLogger(AgentLogStore store, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null) message += Environment.NewLine + exception;
            store.Append(logLevel, category, message);
        }
    }
}
