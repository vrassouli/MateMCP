using MateMCP.Agent.Diagnostics;
using Microsoft.Extensions.Logging;

namespace MateMCP.Agent.Tests;

public sealed class AgentLogStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "matemcp-log-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Ring_buffer_is_bounded_and_supports_incremental_filtered_reads()
    {
        Directory.CreateDirectory(_root);
        var store = new AgentLogStore(Path.Combine(_root, "agent-logs.jsonl"), capacity: 10);
        for (var i = 0; i < 15; i++) store.Append(i % 2 == 0 ? LogLevel.Information : LogLevel.Warning, "Demo.Category", $"message {i}");

        var all = store.Read(limit: 100);
        Assert.Equal(10, all.Count);
        Assert.Equal("message 5", all[0].Message);
        Assert.Equal("message 14", all[^1].Message);

        var after = store.Read(afterId: all[^3].Id, limit: 10);
        Assert.Equal(2, after.Count);
        var warnings = store.Read(minimumLevel: LogLevel.Warning, text: "message");
        Assert.All(warnings, x => Assert.True(x.Level >= LogLevel.Warning));
    }

    [Fact]
    public void Persisted_recent_logs_survive_store_restart()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "agent-logs.jsonl");
        var first = new AgentLogStore(path, capacity: 10);
        first.Append(LogLevel.Error, "MateMCP.Test", "before restart");

        var reopened = new AgentLogStore(path, capacity: 10);
        var entry = Assert.Single(reopened.Read());
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("before restart", entry.Message);
    }

    [Theory]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("api_key=sk-super-secret", "sk-super-secret")]
    [InlineData("refresh-token: refresh123", "refresh123")]
    [InlineData("Using Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature", "eyJhbGciOiJIUzI1NiJ9")]
    public void Sensitive_values_are_redacted_before_storage(string input, string forbidden)
    {
        Directory.CreateDirectory(_root);
        var store = new AgentLogStore(Path.Combine(_root, "agent-logs.jsonl"), capacity: 10);
        store.Append(LogLevel.Warning, "Security", input);

        var entry = Assert.Single(store.Read());
        Assert.DoesNotContain(forbidden, entry.Message, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(forbidden, File.ReadAllText(Path.Combine(_root, "agent-logs.jsonl")), StringComparison.Ordinal);
    }

    [Fact]
    public void Clear_removes_memory_and_persisted_history()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "agent-logs.jsonl");
        var store = new AgentLogStore(path, capacity: 10);
        store.Append(LogLevel.Information, "Test", "hello");
        store.Clear();
        Assert.Empty(store.Read());
        Assert.Equal(string.Empty, File.ReadAllText(path));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
