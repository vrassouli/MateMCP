using System.Text.Json;

namespace MateMCP.Agent.Audit;

public sealed record AuditEntry(DateTimeOffset Timestamp, string Capability, string Target, string Result);

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
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var line = JsonSerializer.Serialize(new AuditEntry(DateTimeOffset.UtcNow, capability, target, result));
        await _gate.WaitAsync(cancellationToken);
        try { await File.AppendAllTextAsync(_path, line + Environment.NewLine, cancellationToken); }
        finally { _gate.Release(); }
    }
}
