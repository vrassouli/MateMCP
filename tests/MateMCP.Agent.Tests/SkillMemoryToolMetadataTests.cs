using System.ComponentModel;
using System.Reflection;
using MateMCP.Agent.Tools;
using ModelContextProtocol.Server;

namespace MateMCP.Agent.Tests;

public sealed class SkillMemoryToolMetadataTests
{
    [Theory]
    [InlineData(nameof(SkillMemoryTools.Search), "memory_search", true, false, true)]
    [InlineData(nameof(SkillMemoryTools.Applicable), "memory_applicable", true, false, true)]
    [InlineData(nameof(SkillMemoryTools.Read), "memory_read", true, false, true)]
    [InlineData(nameof(SkillMemoryTools.Create), "memory_create", false, false, false)]
    [InlineData(nameof(SkillMemoryTools.Update), "memory_update", false, false, true)]
    [InlineData(nameof(SkillMemoryTools.Delete), "memory_delete", false, true, true)]
    public void Memory_tools_are_discoverable_with_explicit_metadata(string methodName, string toolName, bool readOnly, bool destructive, bool idempotent)
    {
        var method = typeof(SkillMemoryTools).GetMethod(methodName)!;
        var tool = method.GetCustomAttribute<McpServerToolAttribute>();
        var description = method.GetCustomAttribute<DescriptionAttribute>();
        Assert.NotNull(tool);
        Assert.Equal(toolName, tool!.Name);
        Assert.Equal(readOnly, tool.ReadOnly);
        Assert.Equal(destructive, tool.Destructive);
        Assert.Equal(idempotent, tool.Idempotent);
        Assert.False(tool.OpenWorld);
        Assert.NotNull(description);
        Assert.Contains("user-managed persistent knowledge store", description!.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never store passwords", description.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Catalog_includes_all_memory_tools()
    {
        foreach (var name in new[] { "memory_search", "memory_applicable", "memory_read", "memory_create", "memory_update", "memory_delete" })
            Assert.Contains(name, McpToolCatalog.Names);
    }

    [Fact]
    public void Companion_exposes_user_management_for_skills_memory()
    {
        var root = FindRepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "Main.razor"));
        var panel = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "SkillsMemoryPanel.razor"));
        Assert.Contains("Skills &amp; Memory", main, StringComparison.Ordinal);
        Assert.Contains("<SkillsMemoryPanel", main, StringComparison.Ordinal);
        Assert.Contains("ProjectContext=", main, StringComparison.Ordinal);
        Assert.Contains("Global", panel, StringComparison.Ordinal);
        Assert.Contains("Project", panel, StringComparison.Ordinal);
        Assert.Contains("Enabled", panel, StringComparison.Ordinal);
        Assert.Contains("source:", panel, StringComparison.Ordinal);
        Assert.Contains("last modified by:", panel, StringComparison.Ordinal);
        Assert.Contains("Created @FormatTime", panel, StringComparison.Ordinal);
        Assert.Contains("Archive", panel, StringComparison.Ordinal);
        Assert.Contains("Unarchive", panel, StringComparison.Ordinal);
        Assert.Contains("Disable", panel, StringComparison.Ordinal);
        Assert.Contains("Delete", panel, StringComparison.Ordinal);
        Assert.Contains("confirm", panel, StringComparison.Ordinal);
        Assert.Contains("Secret Manager", panel, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MateMCP.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
