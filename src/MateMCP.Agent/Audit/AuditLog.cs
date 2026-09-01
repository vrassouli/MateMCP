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

    public async Task<IReadOnlyList<AuditEntry>> ReadCredentialUsageAsync(int limit = 200,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 1000);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return [];
            var entries = new Queue<AuditEntry>(limit);
            foreach (var line in await File.ReadAllLinesAsync(_path, cancellationToken))
            {
                AuditEntry? entry;
                try { entry = JsonSerializer.Deserialize<AuditEntry>(line); }
                catch (JsonException) { continue; }
                if (entry is null || !string.Equals(entry.Capability, "secret.use", StringComparison.Ordinal)) continue;
                if (entries.Count == limit) entries.Dequeue();
                entries.Enqueue(entry);
            }
            return entries.Reverse().ToArray();
        }
        finally { _gate.Release(); }
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
