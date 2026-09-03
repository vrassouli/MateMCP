using System.ComponentModel;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Projects;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class ProjectTools(ProjectRegistry registry, ProjectConfigurationService configuration, AuditLog audit)
{
    private const string Guidance = "MateMCP projects are registered local workspaces owned by this Agent/device. Use these tools to discover project identity instead of inventing project names from arbitrary paths. Project removal is metadata-only and never deletes source files.";

    [McpServerTool(Name = "project_list", Title = "List MateMCP projects", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(Guidance + " Returns stable project IDs, display names, workspace roots, permissions, and current availability.")]
    public IReadOnlyList<ProjectDefinition> List()
        => registry.All.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    [McpServerTool(Name = "project_get", Title = "Get MateMCP project", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(Guidance + " Reads a registered project by stable ID or display name.")]
    public ProjectDefinition Get([Description("Stable project ID or configured display name.")] string project)
        => registry.Get(project);

    [McpServerTool(Name = "project_resolve", Title = "Resolve workspace to MateMCP project", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(Guidance + " Resolves a local filesystem path to the most specific registered project containing it. Returns null when no project owns the path.")]
    public ProjectDefinition? Resolve([Description("Absolute local path on this Agent/device.")] string path)
        => registry.ResolveWorkspace(path);

    [McpServerTool(Name = "project_register", Title = "Register MateMCP project", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(Guidance + " Registers an existing local workspace. Duplicate names and duplicate normalized workspace paths are rejected.")]
    public async Task<ProjectDefinition> Register(string name, string root, bool read = true, bool write = true, bool shell = true,
        CancellationToken cancellationToken = default)
    {
        var project = configuration.Add(new ProjectUpdate(name, root, read, write, shell));
        await audit.WriteAsync("project.register", project.Id, project.Root, cancellationToken);
        return project;
    }

    [McpServerTool(Name = "project_update", Title = "Update MateMCP project", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(Guidance + " Updates project metadata by stable ID or name while preserving its stable identity and project-scoped Skills & Memory references.")]
    public async Task<ProjectDefinition> Update(string project, string name, string root, bool read = true, bool write = true, bool shell = true,
        CancellationToken cancellationToken = default)
    {
        var existing = registry.Get(project);
        var updated = configuration.Update(existing.Name, new ProjectUpdate(name, root, read, write, shell));
        await audit.WriteAsync("project.update", updated.Id, updated.Root, cancellationToken);
        return updated;
    }

    [McpServerTool(Name = "project_unregister", Title = "Unregister MateMCP project", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description(Guidance + " Unregisters project metadata only. It never deletes or modifies files in the workspace.")]
    public async Task<object> Unregister(string project, CancellationToken cancellationToken = default)
    {
        var existing = registry.Get(project);
        var removed = configuration.Remove(existing.Id);
        await audit.WriteAsync("project.unregister", existing.Id, removed ? "removed-metadata-only" : "not-found", cancellationToken);
        return new { removed, filesDeleted = false };
    }
}
