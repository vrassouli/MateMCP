using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Context;
using MateMCP.Agent.Memory;
using MateMCP.Agent.Projects;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Tests;

public sealed class ContextObservabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "matemcp-context-observability-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Bootstrap_events_share_context_id_without_logging_context_content_or_lease()
    {
        Directory.CreateDirectory(_root);
        const string instructionContent = "PRIVATE REPOSITORY INSTRUCTION CONTENT";
        const string skillContent = "PRIVATE RELEASE SKILL CONTENT";
        const string memoryContent = "PRIVATE DURABLE MEMORY CONTENT";
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), instructionContent);

        var skillDirectory = Path.Combine(_root, ".matemcp", "skills", "release");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), $"""
---
name: Release workflow
triggers: [release, package]
---
# Release workflow
{skillContent}
""");

        var options = new MateOptions
        {
            Projects =
            [
                new ProjectOptions { Name = "Demo", Root = _root, Read = true, Write = true, Shell = true }
            ]
        };
        var projects = new ProjectRegistry(new StaticOptionsMonitor<MateOptions>(options));
        var memory = new SkillMemoryStore(projects, Path.Combine(_root, "skills-memory.json"));
        var memoryItem = await memory.CreateAsync(new(
            "Release memory", "memory", "global", null, ["release"], null, memoryContent, "user"));
        var auditPath = Path.Combine(_root, "audit.jsonl");
        var audit = new AuditLog(auditPath);

        var first = await ProjectContextBootstrap.RequireAsync(
            projects, memory, audit, Options.Create(options),
            "shell_exec", "Demo", "package release", null, null);

        Assert.True(first.Required);
        Assert.NotNull(first.Lease);
        Assert.NotNull(first.ContextId);
        Assert.NotNull(first.Context);
        Assert.StartsWith("ctx-", first.ContextId, StringComparison.Ordinal);
        Assert.DoesNotContain(first.Lease!, first.ContextId!, StringComparison.Ordinal);
        Assert.Contains(".matemcp/skills", first.Context!, StringComparison.Ordinal);
        Assert.Contains("Update or deduplicate", first.Context!, StringComparison.Ordinal);
        Assert.Contains("Never store secrets", first.Context!, StringComparison.Ordinal);

        var events = await audit.ReadAsync();
        var instruction = Assert.Single(events, x => x.Capability == "instruction.apply");
        var skill = Assert.Single(events, x => x.Capability == "skill.apply");
        var memoryInjected = Assert.Single(events, x => x.Capability == "memory.inject");
        var bootstrap = Assert.Single(events, x => x.Capability == "context.bootstrap");

        foreach (var entry in new[] { instruction, skill, memoryInjected, bootstrap })
            Assert.Contains($"context:{first.ContextId}", entry.Result, StringComparison.Ordinal);
        Assert.Contains("source:AGENTS.md", instruction.Result, StringComparison.Ordinal);
        Assert.Contains("source:.matemcp/skills/release/SKILL.md", skill.Result, StringComparison.Ordinal);
        Assert.Contains($"ids:{memoryItem.Id}", memoryInjected.Result, StringComparison.Ordinal);

        var second = await ProjectContextBootstrap.RequireAsync(
            projects, memory, audit, Options.Create(options),
            "shell_exec", "Demo", "package release", null, first.Lease);

        Assert.False(second.Required);
        Assert.Equal(first.ContextId, second.ContextId);
        var eventsAfterReuse = await audit.ReadAsync();
        var reuse = Assert.Single(eventsAfterReuse, x => x.Capability == "context.reuse");
        Assert.Contains($"context:{first.ContextId}", reuse.Result, StringComparison.Ordinal);
        Assert.Single(eventsAfterReuse, x => x.Capability == "instruction.apply");
        Assert.Single(eventsAfterReuse, x => x.Capability == "skill.apply");
        Assert.Single(eventsAfterReuse, x => x.Capability == "memory.inject");

        var rawAudit = await File.ReadAllTextAsync(auditPath);
        Assert.DoesNotContain(instructionContent, rawAudit, StringComparison.Ordinal);
        Assert.DoesNotContain(skillContent, rawAudit, StringComparison.Ordinal);
        Assert.DoesNotContain(memoryContent, rawAudit, StringComparison.Ordinal);
        Assert.DoesNotContain(first.Lease!, rawAudit, StringComparison.Ordinal);
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
