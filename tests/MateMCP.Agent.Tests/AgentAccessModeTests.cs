using MateMCP.Agent.Configuration;
using MateMCP.Agent.Security;

namespace MateMCP.Agent.Tests;

public sealed class AgentAccessModeTests
{
    [Fact]
    public async Task AccessModeStore_DefaultsToSafeAndPersistsFullAccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "matemcp-access-mode-tests", Guid.NewGuid().ToString("n"));
        var path = Path.Combine(root, AgentAccessModeStore.FileName);
        var store = new AgentAccessModeStore(path);

        var initial = await store.GetAsync();
        Assert.Equal(AgentAccessMode.Ask, initial.Mode);

        await store.SetAsync(AgentAccessMode.FullAccess);
        var persisted = await new AgentAccessModeStore(path).GetAsync();

        Assert.Equal(AgentAccessMode.FullAccess, persisted.Mode);
        Assert.True(persisted.UpdatedAt > DateTimeOffset.MinValue);
    }

    [Fact]
    public void DefaultStorePath_FollowsAgentConfigurationDataDirectory()
    {
        var expected = Path.Combine(ConfigurationBootstrap.GetUserDataDirectory(), AgentAccessModeStore.FileName);

        Assert.Equal(expected, AgentAccessModeStore.GetDefaultPath());
    }

    [Fact]
    public void ApprovalService_RemovesDecidedRequestsSynchronouslyAndChecksFullAccessBeforePrompting()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Security", "ApprovalService.cs"));
        var store = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Security", "AgentAccessModeStore.cs"));

        Assert.Contains("_pending.TryRemove(id, out _);", service, StringComparison.Ordinal);
        Assert.Contains("accessMode.Mode == AgentAccessMode.FullAccess", service, StringComparison.Ordinal);
        Assert.Contains("allowed:full-access", service, StringComparison.Ordinal);
        Assert.Contains("ConfigurationBootstrap.GetUserDataDirectory()", store, StringComparison.Ordinal);
    }

    [Fact]
    public void Companion_Overview_ExposesExplicitFullAccessWithConfirmationAndUsesAgentDataDirectory()
    {
        var root = FindRepositoryRoot();
        var overview = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "DesktopUpdateOverviewCard.razor"));

        Assert.Contains("Agent access", overview, StringComparison.Ordinal);
        Assert.Contains("FULL ACCESS / UNATTENDED", overview, StringComparison.Ordinal);
        Assert.Contains("Full Access disables interactive approval prompts", overview, StringComparison.Ordinal);
        Assert.Contains("Confirm Full Access", overview, StringComparison.Ordinal);
        Assert.Contains("Safe / Ask", overview, StringComparison.Ordinal);
        Assert.Contains("agent-access-mode.json", overview, StringComparison.Ordinal);
        Assert.Contains("Path.GetDirectoryName(status.Configuration)", overview, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MateMCP.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the MateMCP repository root.");
    }
}
