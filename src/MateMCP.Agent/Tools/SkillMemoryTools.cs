using System.ComponentModel;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Memory;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class SkillMemoryTools(SkillMemoryStore store, AuditLog audit)
{
    private const string Guidance = "MateMCP Skills & Memory is a transparent user-managed global knowledge store for durable cross-project rules, preferences, reusable procedures, and lessons. Project-specific durable knowledge must live in that project's repository as SKILL.md files, preferably under .matemcp/skills/<name>/SKILL.md, so it travels with Git to every MateMCP Agent. Current direct user instructions override persisted context. Never store passwords, tokens, API keys, or other credentials here; use MateMCP Secret Management instead. Prefer updating an existing global item over creating duplicates, and avoid transient/noisy facts.";

    [McpServerTool(Name = "memory_search", Title = "Search global Skills & Memory", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(Guidance + " Search global items by type/text and return compact metadata plus content for matching enabled items.")]
    public async Task<IReadOnlyList<SkillMemoryItem>> Search(
        [Description("Optional item type such as memory, skill, rule, or procedure.")] string? type = null,
        [Description("Optional free-text query matched against title, description, content, and tags.")] string? text = null,
        CancellationToken cancellationToken = default)
    {
        var result = await store.SearchAsync(type: type, text: text, cancellationToken: cancellationToken);
        await audit.WriteAsync("memory.search", ScopeTarget(type), $"matches:{result.Count};queryChars:{text?.Length ?? 0}", cancellationToken);
        return result;
    }

    [McpServerTool(Name = "memory_applicable", Title = "List applicable global Skills & Memory", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(Guidance + " Returns enabled, non-archived global items. Repository project skills are discovered automatically when project-scoped tools run.")]
    public async Task<IReadOnlyList<SkillMemoryItem>> Applicable(CancellationToken cancellationToken = default)
    {
        var result = await store.ApplicableAsync(cancellationToken: cancellationToken);
        await audit.WriteAsync("memory.applicable", "global", $"items:{result.Count}", cancellationToken);
        return result;
    }

    [McpServerTool(Name = "memory_read", Title = "Read global Skills & Memory item", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(Guidance + " Reads one global item by id.")]
    public async Task<SkillMemoryItem> Read(string id, CancellationToken cancellationToken = default)
    {
        var item = await store.GetAsync(id, cancellationToken);
        await audit.WriteAsync("memory.read", item.Id, $"type:{item.Type};scope:global", cancellationToken);
        return item;
    }

    [McpServerTool(Name = "memory_create", Title = "Create global Skills & Memory item", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(Guidance + " Create only durable information useful across projects or devices. Use source=ai for AI-created entries.")]
    public async Task<SkillMemoryItem> Create(string title, string content, string type = "memory",
        string? description = null, string[]? tags = null, string source = "ai", bool enabled = true, CancellationToken cancellationToken = default)
    {
        var item = await store.CreateAsync(new SkillMemoryUpdate(title, type, "global", null, tags, description, content, source, enabled), cancellationToken);
        await audit.WriteAsync("memory.create", item.Id, $"{item.Source}:global", cancellationToken);
        return item;
    }

    [McpServerTool(Name = "memory_update", Title = "Update global Skills & Memory item", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(Guidance + " Prefer this over creating a near-duplicate when an existing global item should be corrected, consolidated, enabled, or disabled.")]
    public async Task<SkillMemoryItem> Update(string id, string title, string content, string type = "memory",
        string? description = null, string[]? tags = null, string source = "ai", bool enabled = true, CancellationToken cancellationToken = default)
    {
        var existing = await store.GetAsync(id, cancellationToken);
        var item = await store.UpdateAsync(id, new SkillMemoryUpdate(title, type, "global", null, tags, description, content, source, enabled, existing.Archived), cancellationToken);
        await audit.WriteAsync("memory.update", item.Id, $"{item.Source}:global", cancellationToken);
        return item;
    }

    [McpServerTool(Name = "memory_delete", Title = "Delete global Skills & Memory item", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description(Guidance + " Permanently deletes one user-visible global item. Use only when deletion is intended; disabling through memory_update is preferable when the knowledge may be useful later.")]
    public async Task<object> Delete(string id, CancellationToken cancellationToken = default)
    {
        var removed = await store.DeleteAsync(id, cancellationToken);
        await audit.WriteAsync("memory.delete", id, removed ? "removed" : "not-found", cancellationToken);
        return new { removed };
    }

    private static string ScopeTarget(string? type)
        => string.IsNullOrWhiteSpace(type) ? "global" : $"global;type:{Bound(type, 32)}";

    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max] + "…";
}
