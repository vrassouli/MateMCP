using System.Text.Json;
using MateMCP.Agent.Audit;

namespace MateMCP.Agent.Tests;

public sealed class AuditLogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MateMCP-AuditTests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadAsync_ReturnsNewestEntriesFirstAndHonorsLimit()
    {
        var path = Path.Combine(_directory, "audit.jsonl");
        var audit = new AuditLog(path);

        await audit.WriteAsync("one", "a", "ok");
        await audit.WriteAsync("two", "b", "ok");
        await audit.WriteAsync("three", "c", "ok");

        var entries = await audit.ReadAsync(2);

        Assert.Collection(entries,
            entry => Assert.Equal("three", entry.Capability),
            entry => Assert.Equal("two", entry.Capability));
    }

    [Fact]
    public async Task ReadAsync_FiltersAtStorageLayerByDateRange()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "audit.jsonl");
        var day = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero);
        var entries = new[]
        {
            new AuditEntry(day.AddDays(-1).AddHours(23), "yesterday", "a", "ok"),
            new AuditEntry(day.AddHours(1), "today-early", "b", "ok"),
            new AuditEntry(day.AddHours(12), "today-late", "c", "ok"),
            new AuditEntry(day.AddDays(1).AddMinutes(1), "tomorrow", "d", "ok")
        };
        await File.WriteAllLinesAsync(path, entries.Select(entry => JsonSerializer.Serialize(entry)));
        var audit = new AuditLog(path);

        var result = await audit.ReadAsync(20, day, day.AddDays(1));

        Assert.Collection(result,
            entry => Assert.Equal("today-late", entry.Capability),
            entry => Assert.Equal("today-early", entry.Capability));
    }

    [Fact]
    public async Task DeleteBeforeAsync_RemovesOnlyOlderAuditEntries()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "audit.jsonl");
        var cutoff = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var entries = new[]
        {
            new AuditEntry(cutoff.AddDays(-2), "old-1", "a", "ok"),
            new AuditEntry(cutoff.AddSeconds(-1), "old-2", "b", "ok"),
            new AuditEntry(cutoff, "keep-1", "c", "ok"),
            new AuditEntry(cutoff.AddDays(1), "keep-2", "d", "ok")
        };
        await File.WriteAllLinesAsync(path, entries.Select(entry => JsonSerializer.Serialize(entry)));
        var audit = new AuditLog(path);

        var deleted = await audit.DeleteBeforeAsync(cutoff);
        var remaining = await audit.ReadAsync(20);

        Assert.Equal(2, deleted);
        Assert.Equal(new[] { "keep-2", "keep-1" }, remaining.Select(x => x.Capability));
    }

    [Fact]
    public async Task CredentialRead_IsNotCrowdedOutByOrdinaryAuditEvents()
    {
        var path = Path.Combine(_directory, "audit.jsonl");
        var audit = new AuditLog(path);

        for (var i = 0; i < 25; i++)
        {
            await audit.WriteCredentialUsageAsync($"credential-{i}", "shell_session_send_secret", $"target-{i}", "injected");
            for (var ordinary = 0; ordinary < 50; ordinary++)
                await audit.WriteAsync("shell.session.write", $"session-{i}-{ordinary}", "chars:1;submit:true");
        }

        var entries = await audit.ReadCredentialUsageAsync(25);

        Assert.Equal(25, entries.Count);
        Assert.Equal("credential-24", entries[0].Credential);
        Assert.Equal("credential-0", entries[^1].Credential);
        Assert.All(entries, entry => Assert.Equal("secret.use", entry.Capability));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch { }
    }
}
