using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MateMCP.Agent.Configuration;
using Microsoft.Extensions.Options;
using Porta.Pty;

namespace MateMCP.Agent.Tools;

public sealed class InteractiveShellSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ShellSession> _sessions = new(StringComparer.Ordinal);
    private readonly InteractiveShellOptions _settings;
    private readonly SemaphoreSlim _sessionSlots;
    private readonly Timer _cleanupTimer;
    private int _disposed;

    public InteractiveShellSessionManager(IOptions<MateOptions> options)
    {
        var configured = options.Value.InteractiveShell ?? new InteractiveShellOptions();
        _settings = new InteractiveShellOptions
        {
            MaxSessions = Math.Clamp(configured.MaxSessions, 1, 64),
            IdleTimeoutSeconds = Math.Clamp(configured.IdleTimeoutSeconds, 1, 86_400),
            MaxLifetimeSeconds = Math.Clamp(configured.MaxLifetimeSeconds, 1, 604_800),
            MaxOutputChars = Math.Clamp(configured.MaxOutputChars, 4_096, 2_000_000),
            MaxInputChars = Math.Clamp(configured.MaxInputChars, 1, 262_144)
        };
        _sessionSlots = new SemaphoreSlim(_settings.MaxSessions, _settings.MaxSessions);
        var cleanupPeriod = TimeSpan.FromSeconds(Math.Clamp(_settings.IdleTimeoutSeconds / 2, 1, 60));
        _cleanupTimer = new Timer(_ => CleanupExpired(), null, cleanupPeriod, cleanupPeriod);
    }

    public int ActiveSessionCount => _sessions.Count;

    public async Task<ShellSessionSnapshot> StartAsync(string command, string workingDirectory, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Command cannot be empty.", nameof(command));
        if (command.Length > 32_768) throw new ArgumentException("Command is too large.", nameof(command));
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException(workingDirectory);

        CleanupExpired();
        if (!await _sessionSlots.WaitAsync(0, ct))
            throw new InvalidOperationException($"Interactive shell session limit ({_settings.MaxSessions}) reached.");

        IPtyConnection? connection = null;
        try
        {
            ThrowIfDisposed();
            var id = Guid.NewGuid().ToString("N");
            connection = await PtyProvider.SpawnAsync(CreateOptions(id, command, workingDirectory), ct);
            var session = new ShellSession(id, command, workingDirectory, connection, _settings.MaxOutputChars, _settings.MaxInputChars);
            if (!_sessions.TryAdd(id, session))
                throw new InvalidOperationException("Could not register interactive shell session.");

            connection = null;
            session.StartReader();
            await Task.Delay(150, CancellationToken.None);
            return session.Snapshot(0);
        }
        catch
        {
            connection?.Dispose();
            _sessionSlots.Release();
            throw;
        }
    }

    public IReadOnlyList<ShellSessionSnapshot> List()
    {
        ThrowIfDisposed();
        CleanupExpired();
        return _sessions.Values
            .Select(session => session.Snapshot(0))
            .OrderByDescending(snapshot => snapshot.LastTouched)
            .ToArray();
    }

    public ShellSessionSnapshot Read(string sessionId, int offset)
    {
        ThrowIfDisposed();
        CleanupExpired();
        return Get(sessionId).Snapshot(Math.Max(0, offset));
    }

    public async Task WriteAsync(string sessionId, string text, bool submit, CancellationToken ct)
    {
        ThrowIfDisposed();
        CleanupExpired();
        await Get(sessionId).WriteAsync(text, submit, ct);
    }

    public async Task WriteSecretAsync(string sessionId, string secret, bool submit, CancellationToken ct)
    {
        ThrowIfDisposed();
        CleanupExpired();
        await Get(sessionId).WriteSecretAsync(secret, submit, ct);
    }

    public bool Close(string sessionId)
    {
        ThrowIfDisposed();
        if (!_sessions.TryRemove(sessionId, out var session)) return false;
        session.Dispose();
        _sessionSlots.Release();
        return true;
    }

    public string GetCommand(string sessionId)
    {
        ThrowIfDisposed();
        return Get(sessionId).Command;
    }

    private ShellSession Get(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException($"Interactive shell session '{sessionId}' was not found or has expired.");
        return session;
    }

    private void CleanupExpired()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var now = DateTimeOffset.UtcNow;
        var idleCutoff = now.AddSeconds(-_settings.IdleTimeoutSeconds);
        var lifetimeCutoff = now.AddSeconds(-_settings.MaxLifetimeSeconds);
        foreach (var pair in _sessions)
        {
            if (pair.Value.LastTouched >= idleCutoff && pair.Value.CreatedAt >= lifetimeCutoff) continue;
            if (!_sessions.TryRemove(pair.Key, out var removed)) continue;
            removed.Dispose();
            _sessionSlots.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static PtyOptions CreateOptions(string id, string command, string workingDirectory)
    {
        string app;
        string[] arguments;
        if (OperatingSystem.IsWindows())
        {
            app = ResolvePowerShell();
            arguments = ["-NoLogo", "-NoProfile", "-Command", command];
        }
        else
        {
            app = File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/sh";
            arguments = ["-lc", command];
        }

        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(x => x.Key is string && x.Value is string)
            .ToDictionary(x => (string)x.Key, x => (string)x.Value!, StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "GITHUB_TOKEN", "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "AZURE_OPENAI_API_KEY", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN" })
            environment.Remove(key);

        return new PtyOptions
        {
            Name = $"MateMCP-{id[..8]}",
            Cols = 120,
            Rows = 30,
            Cwd = workingDirectory,
            App = app,
            CommandLine = arguments,
            Environment = environment,
            UseAsyncIo = true
        };
    }

    private static string ResolvePowerShell()
    {
        var pwsh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        return File.Exists(pwsh) ? pwsh : Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        _cleanupTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _cleanupTimer.Dispose();
        foreach (var pair in _sessions)
        {
            if (!_sessions.TryRemove(pair.Key, out var session)) continue;
            session.Dispose();
            _sessionSlots.Release();
        }
        return ValueTask.CompletedTask;
    }

    private sealed class ShellSession : IDisposable
    {
        private readonly object _sync = new();
        private readonly IPtyConnection _connection;
        private readonly StringBuilder _output = new();
        private readonly List<string> _redactions = [];
        private readonly CancellationTokenSource _readerCts = new();
        private readonly int _maxOutputChars;
        private readonly int _maxInputChars;
        private string _redactionTail = string.Empty;
        private int _trimmedChars;
        private bool _disposed;
        private bool _exited;
        private int? _exitCode;

        public ShellSession(string id, string command, string workingDirectory, IPtyConnection connection, int maxOutputChars, int maxInputChars)
        {
            Id = id;
            Command = command;
            WorkingDirectory = workingDirectory;
            _connection = connection;
            _maxOutputChars = maxOutputChars;
            _maxInputChars = maxInputChars;
            CreatedAt = DateTimeOffset.UtcNow;
            LastTouched = CreatedAt;
            _connection.ProcessExited += (_, e) =>
            {
                lock (_sync)
                {
                    FlushRedactionTail();
                    _exited = true;
                    _exitCode = e.ExitCode;
                }
                Touch();
            };
        }

        public string Id { get; }
        public string Command { get; }
        public string WorkingDirectory { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset LastTouched { get; private set; }

        public void Touch() => LastTouched = DateTimeOffset.UtcNow;
        public void StartReader() => _ = Task.Run(ReadLoopAsync);

        public ShellSessionSnapshot Snapshot(int absoluteOffset)
        {
            lock (_sync)
            {
                var truncated = absoluteOffset < _trimmedChars;
                var start = Math.Clamp(absoluteOffset - _trimmedChars, 0, _output.Length);
                var chunk = _output.ToString(start, _output.Length - start);
                var nextOffset = _trimmedChars + _output.Length;
                return new ShellSessionSnapshot(Id, _connection.Pid, chunk, nextOffset, truncated, _exited, _exitCode, WorkingDirectory, CreatedAt, LastTouched);
            }
        }

        public async Task WriteAsync(string text, bool submit, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ShellSession));
            ValidateInputLength(text);
            var payload = Encoding.UTF8.GetBytes(text + (submit ? "\r" : string.Empty));
            await _connection.WriterStream.WriteAsync(payload, ct);
            await _connection.WriterStream.FlushAsync(ct);
            Touch();
        }

        public async Task WriteSecretAsync(string secret, bool submit, CancellationToken ct)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ShellSession));
            if (string.IsNullOrEmpty(secret)) throw new ArgumentException("Resolved secret is empty.", nameof(secret));
            ValidateInputLength(secret);
            lock (_sync)
            {
                if (!_redactions.Contains(secret, StringComparer.Ordinal)) _redactions.Add(secret);
            }

            var bytes = Encoding.UTF8.GetBytes(secret + (submit ? "\r" : string.Empty));
            try
            {
                await _connection.WriterStream.WriteAsync(bytes, ct);
                await _connection.WriterStream.FlushAsync(ct);
                Touch();
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        private void ValidateInputLength(string text)
        {
            if (text.Length > _maxInputChars)
                throw new ArgumentException($"Interactive shell input exceeds the configured {_maxInputChars}-character limit.");
        }

        private async Task ReadLoopAsync()
        {
            var buffer = new byte[8192];
            try
            {
                while (!_readerCts.IsCancellationRequested)
                {
                    var count = await _connection.ReaderStream.ReadAsync(buffer, _readerCts.Token);
                    if (count <= 0) break;
                    var text = Encoding.UTF8.GetString(buffer, 0, count);
                    lock (_sync) AppendRedacted(text);
                    Touch();
                }
            }
            catch (OperationCanceledException) when (_readerCts.IsCancellationRequested) { }
            catch (ObjectDisposedException) { }
            finally
            {
                lock (_sync) FlushRedactionTail();
            }
        }

        private void AppendRedacted(string text)
        {
            if (_redactions.Count == 0)
            {
                AppendOutput(text);
                return;
            }

            var combined = _redactionTail + text;
            foreach (var secret in _redactions)
                combined = combined.Replace(secret, "[REDACTED]", StringComparison.Ordinal);

            var maxSecretLength = _redactions.Max(x => x.Length);
            var keep = Math.Min(Math.Max(0, maxSecretLength - 1), combined.Length);
            var emitLength = combined.Length - keep;
            if (emitLength > 0) AppendOutput(combined[..emitLength]);
            _redactionTail = keep > 0 ? combined[^keep..] : string.Empty;
        }

        private void FlushRedactionTail()
        {
            if (_redactionTail.Length == 0) return;
            var text = _redactionTail;
            foreach (var secret in _redactions)
                text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            _redactionTail = string.Empty;
            AppendOutput(text);
        }

        private void AppendOutput(string text)
        {
            _output.Append(text);
            if (_output.Length <= _maxOutputChars) return;
            var remove = _output.Length - _maxOutputChars;
            _output.Remove(0, remove);
            _trimmedChars += remove;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _readerCts.Cancel();
            try { if (!_exited) _connection.Kill(); } catch { }
            lock (_sync) FlushRedactionTail();
            _connection.Dispose();
            _readerCts.Dispose();
        }
    }
}

public sealed record ShellSessionSnapshot(
    string SessionId,
    int ProcessId,
    string Output,
    int NextOffset,
    bool OutputTruncated,
    bool Exited,
    int? ExitCode,
    string WorkingDirectory,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastTouched);
