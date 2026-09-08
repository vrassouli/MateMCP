using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Memory;
using MateMCP.Agent.Projects;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Tests;

public sealed class ProactiveMemoryContextTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "matemcp-proactive-memory-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Project_context_is_automatic_isolated_and_audited()
    {
        var (store, audit) = CreateServices();
        await store.CreateAsync(new("Demo deployment", "procedure", "project", "Demo", ["deploy"], null, "Run the Demo release checklist before deploying.", "user"));
        await store.CreateAsync(new("Other deployment", "procedure", "project", "Other", ["deploy"], null, "This belongs only to Other.", "user"));
        await store.CreateAsync(new("Unrelated note", "memory", "global", null, ["finance"], null, "Quarterly budget note.", "user"));

        var context = await ProactiveMemoryContext.BuildAsync(store, audit, "shell_exec", "Demo", "dotnet publish && deploy", CancellationToken.None);

        Assert.NotNull(context);
        Assert.Contains("Demo release checklist", context, StringComparison.Ordinal);
        Assert.DoesNotContain("belongs only to Other", context, StringComparison.Ordinal);
        Assert.DoesNotContain("Quarterly budget", context, StringComparison.Ordinal);
        Assert.Contains("current user instructions override", context, StringComparison.Ordinal);

        var events = await audit.ReadAsync();
        var injected = Assert.Single(events.Where(x => x.Capability == "memory.inject"));
        Assert.Equal("shell_exec:Demo", injected.Target);
        Assert.Contains("items:", injected.Result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Global_context_requires_relevance_or_active_skill_and_is_bounded()
    {
        var (store, audit) = CreateServices();
        await store.CreateAsync(new("Release skill", "skill", "global", null, ["release"], null, new string('x', 10_000), "user"));
        await store.CreateAsync(new("Unrelated memory", "memory", "global", null, ["finance"], null, "Do not inject this for deployment.", "user"));

        var context = await ProactiveMemoryContext.BuildAsync(store, audit, "shell_exec", null, "release package", CancellationToken.None);

        Assert.NotNull(context);
        Assert.True(context.Length <= ProactiveMemoryContext.MaxChars);
        Assert.Contains("Release skill", context, StringComparison.Ordinal);
        Assert.DoesNotContain("Unrelated memory", context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_relevant_global_memory_returns_no_context_and_no_injection_event()
    {
        var (store, audit) = CreateServices();
        await store.CreateAsync(new("Finance note", "memory", "global", null, ["finance"], null, "Budget reminder.", "user"));

        var context = await ProactiveMemoryContext.BuildAsync(store, audit, "shell_exec", null, "dotnet test", CancellationToken.None);

        Assert.Null(context);
        Assert.DoesNotContain(await audit.ReadAsync(), x => x.Capability == "memory.inject");
    }

    private (SkillMemoryStore Store, AuditLog Audit) CreateServices()
    {
        Directory.CreateDirectory(_root);
        var demoRoot = Path.Combine(_root, "demo");
        var otherRoot = Path.Combine(_root, "other");
        Directory.CreateDirectory(demoRoot);
        Directory.CreateDirectory(otherRoot);
        var options = new MateOptions
        {
            Projects =
            [
                new ProjectOptions { Name = "Demo", Root = demoRoot, Read = true, Write = true, Shell = true },
                new ProjectOptions { Name = "Other", Root = otherRoot, Read = true, Write = true, Shell = true }
            ]
        };
        var projects = new ProjectRegistry(new StaticOptionsMonitor<MateOptions>(options));
        return (
            new SkillMemoryStore(projects, Path.Combine(_root, "skills-memory.json")),
            new AuditLog(Path.Combine(_root, "audit.jsonl")));
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
