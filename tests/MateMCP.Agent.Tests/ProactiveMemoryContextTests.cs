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
    public async Task Global_context_is_available_during_project_work_and_is_audited()
    {
        var (store, audit) = CreateServices();
        await store.CreateAsync(new("Deployment convention", "procedure", "global", null, ["deploy"], null, "Run the shared release checklist before deploying.", "user"));
        await store.CreateAsync(new("Unrelated note", "memory", "global", null, ["finance"], null, "Quarterly budget note.", "user"));

        var context = await ProactiveMemoryContext.BuildAsync(store, audit, "shell_exec", "Demo", "dotnet publish && deploy", CancellationToken.None);

        Assert.NotNull(context);
        Assert.Contains("shared release checklist", context, StringComparison.Ordinal);
        Assert.DoesNotContain("Quarterly budget", context, StringComparison.Ordinal);
        Assert.Contains("current user instructions override", context, StringComparison.Ordinal);

        var events = await audit.ReadAsync();
        var injected = Assert.Single(events, x => x.Capability == "memory.inject");
        Assert.Equal("shell_exec:Demo", injected.Target);
        Assert.Contains("items:", injected.Result, StringComparison.Ordinal);
        Assert.Contains("types:procedure:1", injected.Result, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task Off_mode_skips_lookup_output_and_telemetry()
    {
        var (store, audit) = CreateServices();
        await store.CreateAsync(new("Release skill", "skill", "global", null, ["release"], null, "Use the release checklist.", "user"));
        var options = new ProactiveMemoryOptions { Mode = ProactiveMemoryMode.Off };

        var context = await ProactiveMemoryContext.BuildAsync(store, audit, "shell_exec", null, "release package", options, CancellationToken.None);

        Assert.Null(context);
        Assert.DoesNotContain(await audit.ReadAsync(), x => x.Capability is "memory.inject" or "memory.suggest");
    }

    [Fact]
    public async Task Suggested_mode_returns_hint_without_memory_content_and_audits_types()
    {
        var (store, audit) = CreateServices();
        const string durableContent = "PRIVATE DURABLE PROCEDURE CONTENT";
        await store.CreateAsync(new("Release skill", "skill", "global", null, ["release"], null, durableContent, "user"));
        var options = new ProactiveMemoryOptions { Mode = ProactiveMemoryMode.Suggested };

        var context = await ProactiveMemoryContext.BuildAsync(store, audit, "shell_session_start", null, "release package", options, CancellationToken.None);

        Assert.NotNull(context);
        Assert.Contains("relevant durable context", context, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(durableContent, context, StringComparison.Ordinal);
        var suggested = Assert.Single(await audit.ReadAsync(), x => x.Capability == "memory.suggest");
        Assert.Equal("shell_session_start", suggested.Target);
        Assert.Contains("types:skill:1", suggested.Result, StringComparison.OrdinalIgnoreCase);
        var auditText = await File.ReadAllTextAsync(Path.Combine(_root, "audit.jsonl"));
        Assert.DoesNotContain(durableContent, auditText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configured_bounds_are_clamped_and_applied()
    {
        var (store, audit) = CreateServices();
        for (var i = 0; i < 12; i++)
            await store.CreateAsync(new($"Deploy procedure {i}", "procedure", "global", null, ["deploy"], null, new string((char)('a' + i % 20), 2_000), "user"));
        var options = new ProactiveMemoryOptions { Mode = ProactiveMemoryMode.Automatic, MaxItems = 2, MaxChars = 700 };

        var context = await ProactiveMemoryContext.BuildAsync(store, audit, "shell_exec", null, "deploy", options, CancellationToken.None);

        Assert.NotNull(context);
        Assert.True(context.Length <= 700);
        var itemLines = context.Split('\n').Count(x => x.StartsWith("- [", StringComparison.Ordinal));
        Assert.True(itemLines <= 2);
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
