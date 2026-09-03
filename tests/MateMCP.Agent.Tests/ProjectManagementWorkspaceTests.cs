namespace MateMCP.Agent.Tests;

public sealed class ProjectManagementWorkspaceTests
{
    [Fact]
    public void Project_model_has_stable_identity_availability_and_duplicate_workspace_protection()
    {
        var root = FindRepositoryRoot();
        var registry = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Projects", "ProjectRegistry.cs"));
        var config = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Projects", "ProjectConfigurationService.cs"));
        var options = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Configuration", "MateOptions.cs"));
        var memory = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Memory", "SkillMemoryStore.cs"));

        Assert.Contains("string? Id", options, StringComparison.Ordinal);
        Assert.Contains("ProjectDefinition(string Id", registry, StringComparison.Ordinal);
        Assert.Contains("bool Available", registry, StringComparison.Ordinal);
        Assert.Contains("LegacyId", registry, StringComparison.Ordinal);
        Assert.Contains("Guid.NewGuid().ToString(\"N\")", config, StringComparison.Ordinal);
        Assert.Contains("already registered", config, StringComparison.Ordinal);
        Assert.Contains("project = project with { Id = existingId }", config, StringComparison.Ordinal);
        Assert.Contains("MigrateSkillMemoryProjectReferences", config, StringComparison.Ordinal);
        Assert.Contains("return projects.Get(project.Trim()).Id", memory, StringComparison.Ordinal);
        Assert.Contains("MatchesProject", memory, StringComparison.Ordinal);
    }

    [Fact]
    public void Projects_are_exposed_to_ai_and_companion_management()
    {
        var root = FindRepositoryRoot();
        var tools = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Tools", "ProjectTools.cs"));
        var panel = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "ProjectsPanel.razor"));
        var main = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "Main.razor"));

        Assert.Contains("Name = \"project_list\"", tools, StringComparison.Ordinal);
        Assert.Contains("Name = \"project_get\"", tools, StringComparison.Ordinal);
        Assert.Contains("Name = \"project_resolve\"", tools, StringComparison.Ordinal);
        Assert.Contains("Name = \"project_register\"", tools, StringComparison.Ordinal);
        Assert.Contains("Name = \"project_update\"", tools, StringComparison.Ordinal);
        Assert.Contains("Name = \"project_unregister\"", tools, StringComparison.Ordinal);
        Assert.Contains("filesDeleted = false", tools, StringComparison.Ordinal);
        Assert.Contains("Add Project", panel, StringComparison.Ordinal);
        Assert.Contains("Search projects", panel, StringComparison.Ordinal);
        Assert.Contains("Edit Project", panel, StringComparison.Ordinal);
        Assert.Contains("Unregister only; source files will not be deleted.", panel, StringComparison.Ordinal);
        Assert.Contains("Missing", panel, StringComparison.Ordinal);
        Assert.Contains("Section == \"projects\"", main, StringComparison.Ordinal);
        Assert.Contains("<ProjectsPanel />", main, StringComparison.Ordinal);
        Assert.Contains("\"projects\" => \"Projects\"", main, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MateMCP.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
