using System.ComponentModel;
using MateMCP.Agent.Projects;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tools;

[McpServerToolType]
public sealed class ProjectTools(ProjectRegistry registry)
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
}
