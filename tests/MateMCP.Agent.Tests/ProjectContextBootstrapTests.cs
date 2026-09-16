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
