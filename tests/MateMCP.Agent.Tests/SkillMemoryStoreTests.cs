using System.Text.Json;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Context;
using MateMCP.Agent.Memory;
using MateMCP.Agent.Projects;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Tests;

public sealed class SkillMemoryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "matemcp-memory-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Persists_global_items_and_returns_only_global_applicable_context()
    {
        Directory.CreateDirectory(_root);
        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        var store = CreateStore(projectRoot);

        await store.CreateAsync(new("Global rule", "rule", "global", null, ["style"], null, "Use concise commit messages.", "ai"));

        var reopened = CreateStore(projectRoot);
        var applicable = await reopened.ApplicableAsync("Demo");

        var item = Assert.Single(applicable);
        Assert.Equal("global", item.Scope);
        Assert.Null(item.Project);
    }

    [Fact]
    public async Task Project_scope_is_rejected_and_points_to_repository_skills()
    {
        Directory.CreateDirectory(_root);
        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        var store = CreateStore(projectRoot);

        var create = await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(
            new("Project note", "memory", "project", "Demo", null, null, "demo-only", "ai")));
        Assert.Contains("repository", create.Message, StringComparison.OrdinalIgnoreCase);

        var search = await Assert.ThrowsAsync<ArgumentException>(() => store.SearchAsync("project", "Demo"));
        Assert.Contains("repository", search.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Legacy_project_items_migrate_to_repository_skills_and_are_removed_from_active_store()
    {
        Directory.CreateDirectory(_root);
        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        var path = Path.Combine(_root, "skills-memory.json");
        var now = DateTimeOffset.UtcNow;
        var items = new[]
        {
            new SkillMemoryItem("global1", "Global rule", "rule", "global", null, ["style"], null, "Global content", "user", "user", true, now, now),
            new SkillMemoryItem("project12345678", "Release signing", "procedure", "project", "demo-stable-id", ["release", "signing"], "Release procedure", "Artifacts must be signed before publication.", "user", "user", true, now, now)
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(items, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        var store = CreateStore(projectRoot);
        var result = await store.MigrateLegacyProjectItemsAsync();

        Assert.Equal(1, result.Found);
        Assert.Equal(1, result.MigratedToRepository);
        Assert.Equal(0, result.ArchivedOnly);
        Assert.NotNull(result.ArchivePath);
        Assert.True(File.Exists(result.ArchivePath));

        var active = await store.SearchAsync(includeDisabled: true);
        var global = Assert.Single(active);
        Assert.Equal("global1", global.Id);

        var skillPath = Assert.Single(Directory.GetFiles(Path.Combine(projectRoot, ".matemcp", "skills"), "SKILL.md", SearchOption.AllDirectories));
        var skill = await File.ReadAllTextAsync(skillPath);
        Assert.Contains("Release signing", skill, StringComparison.Ordinal);
        Assert.Contains("Artifacts must be signed before publication.", skill, StringComparison.Ordinal);
        Assert.Contains("release", skill, StringComparison.OrdinalIgnoreCase);

        var project = CreateProjects(projectRoot).Get("Demo");
        var selected = ProjectSkillContext.Build(project, "package release", null);
        Assert.Contains("Artifacts must be signed before publication.", selected.Context, StringComparison.Ordinal);

        var archived = await File.ReadAllTextAsync(result.ArchivePath!);
        Assert.Contains("project12345678", archived, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_project_items_for_unavailable_project_are_archived_without_data_loss()
    {
        Directory.CreateDirectory(_root);
        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        var path = Path.Combine(_root, "skills-memory.json");
        var now = DateTimeOffset.UtcNow;
        var items = new[]
        {
            new SkillMemoryItem("orphan1", "Orphan project note", "memory", "project", "missing-project-id", ["legacy"], null, "Keep this content recoverable.", "user", "user", true, now, now)
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(items, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        var store = CreateStore(projectRoot);
        var result = await store.MigrateLegacyProjectItemsAsync();

        Assert.Equal(1, result.Found);
        Assert.Equal(0, result.MigratedToRepository);
        Assert.Equal(1, result.ArchivedOnly);
        Assert.Empty(await store.SearchAsync(includeDisabled: true));
        Assert.Contains("Keep this content recoverable.", await File.ReadAllTextAsync(result.ArchivePath!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabled_or_archived_legacy_project_items_are_archived_but_not_activated_as_repository_skills()
    {
        Directory.CreateDirectory(_root);
        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        var path = Path.Combine(_root, "skills-memory.json");
        var now = DateTimeOffset.UtcNow;
        var items = new[]
        {
            new SkillMemoryItem("disabled1", "Disabled project note", "rule", "project", "Demo", ["always"], null, "Must stay inactive.", "user", "user", false, now, now),
            new SkillMemoryItem("archived1", "Archived project note", "rule", "project", "Demo", ["always"], null, "Must stay archived.", "user", "user", true, now, now, true)
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(items, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        var store = CreateStore(projectRoot);
        var result = await store.MigrateLegacyProjectItemsAsync();

        Assert.Equal(2, result.Found);
        Assert.Equal(0, result.MigratedToRepository);
        Assert.Equal(2, result.ArchivedOnly);
        Assert.False(Directory.Exists(Path.Combine(projectRoot, ".matemcp", "skills")));
        var archive = await File.ReadAllTextAsync(result.ArchivePath!);
        Assert.Contains("disabled1", archive, StringComparison.Ordinal);
        Assert.Contains("archived1", archive, StringComparison.Ordinal);
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

    [Fact]
    public async Task Archived_items_remain_visible_to_management_but_are_not_applicable()
    {
        Directory.CreateDirectory(_root);
        var projectRoot = Path.Combine(_root, "project");
        Directory.CreateDirectory(projectRoot);
        var store = CreateStore(projectRoot);

        var item = await store.CreateAsync(new("Archived rule", "rule", "global", null, ["old"], null, "Do not apply this anymore.", "user"));
        var archived = await store.UpdateAsync(item.Id,
            new("Archived rule", "rule", "global", null, ["old"], null, "Do not apply this anymore.", "user", true, true));

        Assert.True(archived.Archived);
        Assert.Single(await store.SearchAsync(includeDisabled: true));
        Assert.Empty(await store.ApplicableAsync());

        var reopened = CreateStore(projectRoot);
        var persisted = await reopened.GetAsync(item.Id);
        Assert.True(persisted.Archived);
    }

    private SkillMemoryStore CreateStore(string projectRoot)
        => new(CreateProjects(projectRoot), Path.Combine(_root, "skills-memory.json"));

    private static ProjectRegistry CreateProjects(string projectRoot)
    {
        var options = new MateOptions { Projects = [new ProjectOptions { Id = "demo-stable-id", Name = "Demo", Root = projectRoot, Read = true, Write = true, Shell = true }] };
        return new ProjectRegistry(new StaticOptionsMonitor<MateOptions>(options));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
