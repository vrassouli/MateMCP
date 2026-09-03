using System.ComponentModel;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Memory;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class SkillMemoryTools(SkillMemoryStore store, AuditLog audit)
{
    private const string Guidance = "MateMCP Skills & Memory is a transparent user-managed persistent knowledge store for stable rules, project conventions, reusable procedures, and lessons worth reusing across future sessions. Read relevant items when prior context may matter; prefer targeted search over loading everything. Project-scoped items apply only to that configured project, while global items are reusable across projects. Current direct user instructions override persisted items and project items normally override global items. Never store passwords, tokens, API keys, or other credentials here; use MateMCP Secret Management instead. Prefer updating an existing item over creating duplicates, and avoid transient/noisy facts.";

    [McpServerTool(Name = "memory_search", Title = "Search Skills & Memory", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(Guidance + " Search by scope/project/type/text and return compact metadata plus content for matching enabled items.")]
    public Task<IReadOnlyList<SkillMemoryItem>> Search(
        [Description("Optional scope: global or project.")] string? scope = null,
        [Description("Configured MateMCP project name when filtering project-scoped knowledge.")] string? project = null,
        [Description("Optional item type such as memory, skill, rule, or procedure.")] string? type = null,
        [Description("Optional free-text query matched against title, description, content, and tags.")] string? text = null,
        CancellationToken cancellationToken = default)
        => store.SearchAsync(scope, project, type, text, false, cancellationToken);

    [McpServerTool(Name = "memory_applicable", Title = "List applicable Skills & Memory", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(Guidance + " Returns enabled global items plus enabled items for one configured project. Project items are ordered before global items to make precedence explicit.")]
    public Task<IReadOnlyList<SkillMemoryItem>> Applicable([Description("Optional configured MateMCP project name for current work.")] string? project = null,
        CancellationToken cancellationToken = default) => store.ApplicableAsync(project, cancellationToken);

    [McpServerTool(Name = "memory_read", Title = "Read Skills & Memory item", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(Guidance + " Reads one item by id.")]
    public Task<SkillMemoryItem> Read(string id, CancellationToken cancellationToken = default) => store.GetAsync(id, cancellationToken);

    [McpServerTool(Name = "memory_create", Title = "Create Skills & Memory item", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(Guidance + " Create only durable information likely to help future sessions. Use source=ai for AI-created entries.")]
    public async Task<SkillMemoryItem> Create(string title, string content, string type = "memory", string scope = "global", string? project = null,
        string? description = null, string[]? tags = null, string source = "ai", bool enabled = true, CancellationToken cancellationToken = default)
    {
        var item = await store.CreateAsync(new SkillMemoryUpdate(title, type, scope, project, tags, description, content, source, enabled), cancellationToken);
        await audit.WriteAsync("memory.create", item.Id, $"{item.Source}:{item.Scope}:{item.Project ?? "global"}", cancellationToken);
        return item;
    }

    [McpServerTool(Name = "memory_update", Title = "Update Skills & Memory item", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(Guidance + " Prefer this over creating a near-duplicate when an existing item should be corrected, consolidated, enabled/disabled, or moved in scope.")]
    public async Task<SkillMemoryItem> Update(string id, string title, string content, string type = "memory", string scope = "global", string? project = null,
        string? description = null, string[]? tags = null, string source = "ai", bool enabled = true, CancellationToken cancellationToken = default)
    {
        var item = await store.UpdateAsync(id, new SkillMemoryUpdate(title, type, scope, project, tags, description, content, source, enabled), cancellationToken);
        await audit.WriteAsync("memory.update", item.Id, $"{item.Source}:{item.Scope}:{item.Project ?? "global"}", cancellationToken);
        return item;
    }

    [McpServerTool(Name = "memory_delete", Title = "Delete Skills & Memory item", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description(Guidance + " Permanently deletes one user-visible item. Use only when deletion is intended; disabling through memory_update is preferable when the knowledge may be useful later.")]
    public async Task<object> Delete(string id, CancellationToken cancellationToken = default)
    {
        var removed = await store.DeleteAsync(id, cancellationToken);
        await audit.WriteAsync("memory.delete", id, removed ? "removed" : "not-found", cancellationToken);
        return new { removed };
    }
}
