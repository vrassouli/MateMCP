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
