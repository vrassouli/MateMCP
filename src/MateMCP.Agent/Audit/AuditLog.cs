using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace MateMCP.Agent.Audit;

public sealed record AuditEntry(
    DateTimeOffset Timestamp,
    string Capability,
    string Target,
    string Result,
    string? Credential = null,
    string? Tool = null);

public sealed class AuditLog
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public AuditLog() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MateMCP", "audit.jsonl")) { }

    public AuditLog(string path)
    {
        _path = path;
    }

    public async Task WriteAsync(string capability, string target, string result, CancellationToken cancellationToken = default)
        => await AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, capability, target, result), cancellationToken);

    public async Task WriteCredentialUsageAsync(string credential, string tool, string target, string result,
        CancellationToken cancellationToken = default)
        => await AppendAsync(new AuditEntry(DateTimeOffset.UtcNow, "secret.use", target, result, credential, tool), cancellationToken);

    public Task<IReadOnlyList<AuditEntry>> ReadAsync(int limit = 200, CancellationToken cancellationToken = default)
        => ReadMatchingAsync(limit, null, null, static _ => true, cancellationToken);

    public Task<IReadOnlyList<AuditEntry>> ReadAsync(int limit, DateTimeOffset? from, DateTimeOffset? to,
        CancellationToken cancellationToken = default)
        => ReadMatchingAsync(limit, from, to, static _ => true, cancellationToken);

    public Task<IReadOnlyList<AuditEntry>> ReadCredentialUsageAsync(int limit = 200,
        CancellationToken cancellationToken = default)
        => ReadMatchingAsync(limit, null, null, static entry => string.Equals(entry.Capability, "secret.use", StringComparison.Ordinal), cancellationToken);

    public async Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return 0;

            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var temp = _path + ".cleanup-" + Guid.NewGuid().ToString("N");
            var deleted = 0;

            try
            {
                // Keep all file handles in a nested scope so they are disposed before the atomic
                // replacement. Windows will reject File.Move(overwrite) while either file is open.
                {
                    await using var source = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 81920, leaveOpen: false);
                    await using var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await using var writer = new StreamWriter(target, new UTF8Encoding(false), 81920, leaveOpen: false);

                    while (await reader.ReadLineAsync(cancellationToken) is { } line)
                    {
                        AuditEntry? entry;
                        try { entry = JsonSerializer.Deserialize<AuditEntry>(line); }
                        catch (JsonException)
                        {
                            // Preserve malformed legacy lines rather than silently deleting them as part of cleanup.
                            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                            continue;
                        }

                        if (entry is not null && entry.Timestamp < cutoff)
                        {
                            deleted++;
                            continue;
                        }

                        await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                    }

                    await writer.FlushAsync(cancellationToken);
                }

                File.Move(temp, _path, overwrite: true);
                return deleted;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<AuditEntry>> ReadMatchingAsync(int limit, DateTimeOffset? from, DateTimeOffset? to,
        Func<AuditEntry, bool> predicate, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 1000);
        if (from is not null && to is not null && from >= to)
            throw new ArgumentException("Audit range start must be earlier than range end.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return [];
            var entries = new List<AuditEntry>(limit);

            // Audit JSONL is append-only and chronological. Reading from the tail lets the common
            // "Today" view stop as soon as it crosses the requested start date instead of loading
            // or scanning the entire historical file into memory.
            await foreach (var line in ReadLinesNewestFirstAsync(_path, cancellationToken))
            {
                AuditEntry? entry;
                try { entry = JsonSerializer.Deserialize<AuditEntry>(line); }
                catch (JsonException) { continue; }
                if (entry is null) continue;

                if (to is not null && entry.Timestamp >= to.Value) continue;
                if (from is not null && entry.Timestamp < from.Value) break;
                if (!predicate(entry)) continue;

                entries.Add(entry);
                if (entries.Count == limit) break;
            }

            return entries;
        }
        finally { _gate.Release(); }
    }

    private static async IAsyncEnumerable<string> ReadLinesNewestFirstAsync(string path,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length == 0) yield break;

        const int bufferSize = 8192;
        var buffer = new byte[bufferSize];
        var reversedLine = new List<byte>(512);
        var position = stream.Length;

        while (position > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(bufferSize, position);
            position -= count;
            stream.Position = position;

            var read = 0;
            while (read < count)
            {
                var chunk = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken);
                if (chunk == 0) break;
                read += chunk;
            }

            for (var i = read - 1; i >= 0; i--)
            {
                if (buffer[i] == (byte)'\n')
                {
                    if (reversedLine.Count == 0) continue;
                    yield return DecodeReversedLine(reversedLine);
                    reversedLine.Clear();
                    continue;
                }

                reversedLine.Add(buffer[i]);
            }
        }

        if (reversedLine.Count > 0)
            yield return DecodeReversedLine(reversedLine);
    }

    private static string DecodeReversedLine(List<byte> reversed)
    {
        var bytes = reversed.ToArray();
        Array.Reverse(bytes);
        var line = Encoding.UTF8.GetString(bytes);
        return line.EndsWith('\r') ? line[..^1] : line;
    }

    private async Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var line = JsonSerializer.Serialize(entry);
        await _gate.WaitAsync(cancellationToken);
        try { await File.AppendAllTextAsync(_path, line + Environment.NewLine, cancellationToken); }
        finally { _gate.Release(); }
    }
}
