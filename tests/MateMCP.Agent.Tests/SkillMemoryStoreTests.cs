using MateMCP.Agent.Configuration;
using MateMCP.Agent.Memory;
using MateMCP.Agent.Projects;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Tests;

public sealed class SkillMemoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "matemcp-memory-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Persists_global_and_project_items_and_returns_applicable_precedence()
    {
        Directory.CreateDirectory(_root);
        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        var store = CreateStore(projectRoot);

        await store.CreateAsync(new("Global rule", "rule", "global", null, ["style"], null, "Use concise commit messages.", "ai"));
        await store.CreateAsync(new("Project rule", "rule", "project", "Demo", ["style"], null, "Use feature folders.", "user"));

        var reopened = CreateStore(projectRoot);
        var applicable = await reopened.ApplicableAsync("Demo");

        Assert.Equal(2, applicable.Count);
        Assert.Equal("project", applicable[0].Scope);
        Assert.Equal("global", applicable[1].Scope);
    }

    [Fact]
    public async Task Project_scope_requires_configured_project_and_isolated_search()
    {
        Directory.CreateDirectory(_root);
        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        var store = CreateStore(projectRoot);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(new("Bad", "memory", "project", "Other", null, null, "x", "ai")));
        await store.CreateAsync(new("Demo", "memory", "project", "Demo", null, null, "demo-only", "ai"));

        var project = await store.SearchAsync("project", "Demo");
        var global = await store.SearchAsync("global");
        Assert.Single(project);
        Assert.Empty(global);
    }

    [Fact]
    public async Task Update_disable_delete_and_secret_guard_work()
    {
        Directory.CreateDirectory(_root);
        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        var store = CreateStore(projectRoot);
        var item = await store.CreateAsync(new("Skill", "skill", "global", null, null, null, "Do the stable thing.", "ai"));

        var updated = await store.UpdateAsync(item.Id, new("Skill", "skill", "global", null, ["ops"], "desc", "Updated content", "user", false));
        Assert.False(updated.Enabled);
        Assert.Equal("ai", updated.Source);
        Assert.Equal("user", updated.UpdatedBy);
        Assert.Empty(await store.SearchAsync());
        Assert.Single(await store.SearchAsync(includeDisabled: true));
        Assert.True(await store.DeleteAsync(item.Id));
        Assert.Empty(await store.SearchAsync(includeDisabled: true));

        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(new("Secret", "memory", "global", null, null, null, "password=hunter2", "ai")));
    }

    private SkillMemoryStore CreateStore(string projectRoot)
    {
        var options = new MateOptions { Projects = [new ProjectOptions { Name = "Demo", Root = projectRoot, Read = true, Write = true, Shell = true }] };
        return new SkillMemoryStore(new ProjectRegistry(new StaticOptionsMonitor<MateOptions>(options)), Path.Combine(_root, "skills-memory.json"));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
