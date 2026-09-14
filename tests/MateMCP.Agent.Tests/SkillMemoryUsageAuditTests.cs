using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Memory;
using MateMCP.Agent.Projects;
using MateMCP.Agent.Tools;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Tests;

public sealed class SkillMemoryUsageAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "matemcp-memory-usage-audit-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Search_applicable_and_read_emit_content_free_consultation_events()
    {
        Directory.CreateDirectory(_root);
        var projectRoot = Path.Combine(_root, "demo");
        Directory.CreateDirectory(projectRoot);
        var options = new MateOptions
        {
            Projects = [new ProjectOptions { Name = "Demo", Root = projectRoot, Read = true, Write = true, Shell = true }]
        };
        var projects = new ProjectRegistry(new StaticOptionsMonitor<MateOptions>(options));
        var store = new SkillMemoryStore(projects, Path.Combine(_root, "skills-memory.json"));
        var auditPath = Path.Combine(_root, "audit.jsonl");
        var audit = new AuditLog(auditPath);
        var tools = new SkillMemoryTools(store, audit);
        const string privateContent = "PRIVATE DURABLE MEMORY CONTENT";
        const string privateQuery = "PRIVATE-QUERY-TEXT";
        var item = await store.CreateAsync(new("Deployment rule", "rule", "project", "Demo", ["deploy"], null, privateContent, "user"));

        var searched = await tools.Search(project: "Demo", type: "rule", text: privateQuery);
        var applicable = await tools.Applicable("Demo");
        var read = await tools.Read(item.Id);

        Assert.Empty(searched);
        Assert.Contains(applicable, x => x.Id == item.Id);
        Assert.Equal(item.Id, read.Id);

        var events = await audit.ReadAsync();
        var search = Assert.Single(events, x => x.Capability == "memory.search");
        Assert.Contains("matches:0", search.Result, StringComparison.Ordinal);
        Assert.Contains($"queryChars:{privateQuery.Length}", search.Result, StringComparison.Ordinal);
        Assert.Contains("project:Demo", search.Target, StringComparison.Ordinal);
        Assert.Contains("type:rule", search.Target, StringComparison.Ordinal);

        var applicableEvent = Assert.Single(events, x => x.Capability == "memory.applicable");
        Assert.Equal("project:Demo", applicableEvent.Target);
        Assert.Contains("items:", applicableEvent.Result, StringComparison.Ordinal);

        var readEvent = Assert.Single(events, x => x.Capability == "memory.read");
        Assert.Equal(item.Id, readEvent.Target);
        Assert.Contains("type:rule", readEvent.Result, StringComparison.Ordinal);
        Assert.Contains("scope:project", readEvent.Result, StringComparison.Ordinal);

        var rawAudit = await File.ReadAllTextAsync(auditPath);
        Assert.DoesNotContain(privateContent, rawAudit, StringComparison.Ordinal);
        Assert.DoesNotContain(privateQuery, rawAudit, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
