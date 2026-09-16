using System.Text.Json;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Context;
using MateMCP.Agent.Memory;
using MateMCP.Agent.Projects;
using MateMCP.Agent.Tools;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Tests;

public sealed class ProjectContextBootstrapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "matemcp-context-bootstrap-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Filesystem_write_requires_context_before_touching_file_then_accepts_lease()
    {
        var (options, projects, memory, audit) = CreateServices();
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), "Always read repository instructions before changing files.");
        var tools = new FileSystemTools(projects, memory, audit, Options.Create(options));
        var target = Path.Combine(_root, "output.txt");

        var first = await tools.Write("Demo", "output.txt", "hello");
        var firstJson = JsonSerializer.Serialize(first);

        Assert.Contains("context_required", firstJson, StringComparison.Ordinal);
        Assert.Contains("AGENTS.md", firstJson, StringComparison.Ordinal);
        Assert.Contains("Always read repository instructions", firstJson, StringComparison.Ordinal);
        Assert.False(File.Exists(target));

        using var document = JsonDocument.Parse(firstJson);
        var lease = document.RootElement.GetProperty("contextLease").GetString();
        Assert.False(string.IsNullOrWhiteSpace(lease));

        var second = await tools.Write("Demo", "output.txt", "hello", lease);
        var secondJson = JsonSerializer.Serialize(second);

        Assert.Contains("written", secondJson, StringComparison.Ordinal);
        Assert.True(File.Exists(target));
        Assert.Equal("hello", await File.ReadAllTextAsync(target));

        var events = await audit.ReadAsync();
        Assert.Contains(events, x => x.Capability == "context.bootstrap");
        Assert.Contains(events, x => x.Capability == "context.reuse");
    }

    [Fact]
    public async Task Changed_repository_instructions_invalidate_existing_lease()
    {
        var (options, projects, memory, audit) = CreateServices();
        var agents = Path.Combine(_root, "AGENTS.md");
        await File.WriteAllTextAsync(agents, "Rule version one.");

        var first = await ProjectContextBootstrap.RequireAsync(
            projects, memory, audit, Options.Create(options),
            "filesystem_write", "Demo", "file.txt", "file.txt", null);

        Assert.True(first.Required);
        Assert.NotNull(first.Lease);
        Assert.Contains("Rule version one", first.Context, StringComparison.Ordinal);

        await File.WriteAllTextAsync(agents, "Rule version two.");
        var second = await ProjectContextBootstrap.RequireAsync(
            projects, memory, audit, Options.Create(options),
            "filesystem_write", "Demo", "file.txt", "file.txt", first.Lease);

        Assert.True(second.Required);
        Assert.NotEqual(first.ContextHash, second.ContextHash);
        Assert.NotEqual(first.Lease, second.Lease);
        Assert.Contains("Rule version two", second.Context, StringComparison.Ordinal);
        Assert.Contains(await audit.ReadAsync(), x => x.Capability == "context.invalidated");
    }

    [Fact]
    public async Task Nested_agents_files_are_applied_from_root_to_target_scope()
    {
        var (options, projects, memory, audit) = CreateServices();
        await File.WriteAllTextAsync(Path.Combine(_root, "AGENTS.md"), "Root repository rule.");
        var nested = Path.Combine(_root, "src", "feature");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "AGENTS.md"), "Feature-specific rule.");

        var result = await ProjectContextBootstrap.RequireAsync(
            projects, memory, audit, Options.Create(options),
            "filesystem_write", "Demo", "src/feature/file.txt", "src/feature/file.txt", null);

        Assert.True(result.Required);
        Assert.Equal(new[] { "AGENTS.md", "src/feature/AGENTS.md" }, result.Sources);
        Assert.Contains("Root repository rule", result.Context, StringComparison.Ordinal);
        Assert.Contains("Feature-specific rule", result.Context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Matching_project_skill_and_memory_are_automatic_but_unrelated_context_is_not()
    {
        var (options, projects, memory, audit) = CreateServices();
        var releaseSkill = Path.Combine(_root, ".agents", "skills", "release");
        var financeSkill = Path.Combine(_root, ".agents", "skills", "finance");
        Directory.CreateDirectory(releaseSkill);
        Directory.CreateDirectory(financeSkill);
        await File.WriteAllTextAsync(Path.Combine(releaseSkill, "SKILL.md"), """
---
name: Release workflow
description: Package and publish releases
triggers: [release, publish, package]
---
# Release workflow
Run the signed release checklist before publishing artifacts.
""");
        await File.WriteAllTextAsync(Path.Combine(financeSkill, "SKILL.md"), """
---
name: Finance reporting
triggers: [finance, invoice]
---
Never include this in a release task.
""");
        await memory.CreateAsync(new(
            "Release signing requirement", "memory", "project", "Demo", ["release", "signing"], null,
            "Release artifacts must be signed before publication.", "user"));
        await memory.CreateAsync(new(
            "Quarterly finance reminder", "memory", "project", "Demo", ["finance"], null,
            "Prepare the quarterly finance spreadsheet.", "user"));

        var first = await ProjectContextBootstrap.RequireAsync(
            projects, memory, audit, Options.Create(options),
            "shell_exec", "Demo", "dotnet publish && package release", null, null);

        Assert.True(first.Required);
        Assert.Contains(".agents/skills/release/SKILL.md", first.Sources);
        Assert.DoesNotContain(".agents/skills/finance/SKILL.md", first.Sources);
        Assert.Contains("signed release checklist", first.Context, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Release artifacts must be signed", first.Context, StringComparison.Ordinal);
        Assert.DoesNotContain("quarterly finance", first.Context, StringComparison.OrdinalIgnoreCase);

        var eventsAfterFirst = await audit.ReadAsync();
        Assert.Single(eventsAfterFirst, x => x.Capability == "skill.apply");
        Assert.Single(eventsAfterFirst, x => x.Capability == "memory.inject");

        var second = await ProjectContextBootstrap.RequireAsync(
            projects, memory, audit, Options.Create(options),
            "shell_exec", "Demo", "dotnet publish && package release", null, first.Lease);

        Assert.False(second.Required);
        var eventsAfterReuse = await audit.ReadAsync();
        Assert.Single(eventsAfterReuse, x => x.Capability == "skill.apply");
        Assert.Single(eventsAfterReuse, x => x.Capability == "memory.inject");
        Assert.Contains(eventsAfterReuse, x => x.Capability == "context.reuse");
    }

    [Fact]
    public async Task Changing_a_matched_skill_invalidates_the_context_lease()
    {
        var (options, projects, memory, audit) = CreateServices();
        var skillDirectory = Path.Combine(_root, ".matemcp", "skills", "deploy");
        Directory.CreateDirectory(skillDirectory);
        var skillPath = Path.Combine(skillDirectory, "SKILL.md");
        await File.WriteAllTextAsync(skillPath, "# Deploy\nDeploy using procedure version one.");

        var first = await ProjectContextBootstrap.RequireAsync(
            projects, memory, audit, Options.Create(options),
            "shell_exec", "Demo", "deploy application", null, null);
        Assert.True(first.Required);
        Assert.Contains("procedure version one", first.Context, StringComparison.Ordinal);

        await File.WriteAllTextAsync(skillPath, "# Deploy\nDeploy using procedure version two.");
        var second = await ProjectContextBootstrap.RequireAsync(
            projects, memory, audit, Options.Create(options),
            "shell_exec", "Demo", "deploy application", null, first.Lease);

        Assert.True(second.Required);
        Assert.NotEqual(first.ContextHash, second.ContextHash);
        Assert.Contains("procedure version two", second.Context, StringComparison.Ordinal);
        Assert.Contains(await audit.ReadAsync(), x => x.Capability == "context.invalidated");
    }

    [Fact]
    public async Task Unrelated_project_memory_does_not_force_context_injection()
    {
        var (options, projects, memory, audit) = CreateServices();
        await memory.CreateAsync(new(
            "Finance-only note", "memory", "project", "Demo", ["finance"], null,
            "Only relevant to invoices and finance reports.", "user"));

        var result = await ProjectContextBootstrap.RequireAsync(
            projects, memory, audit, Options.Create(options),
            "shell_exec", "Demo", "dotnet test", null, null);

        Assert.False(result.Required);
        Assert.Null(result.Context);
        Assert.DoesNotContain(await audit.ReadAsync(), x => x.Capability == "memory.inject");
    }

    private (MateOptions Options, ProjectRegistry Projects, SkillMemoryStore Memory, AuditLog Audit) CreateServices()
    {
        Directory.CreateDirectory(_root);
        var options = new MateOptions
        {
            Projects =
            [
                new ProjectOptions { Name = "Demo", Root = _root, Read = true, Write = true, Shell = true }
            ]
        };
        var projects = new ProjectRegistry(new StaticOptionsMonitor<MateOptions>(options));
        var memory = new SkillMemoryStore(projects, Path.Combine(_root, "skills-memory.json"));
        var audit = new AuditLog(Path.Combine(_root, "audit.jsonl"));
        return (options, projects, memory, audit);
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
