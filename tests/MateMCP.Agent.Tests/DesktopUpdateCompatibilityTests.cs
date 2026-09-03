namespace MateMCP.Agent.Tests;

public sealed class DesktopUpdateCompatibilityTests
{
    [Fact]
    public void Companion_verifies_running_agent_capabilities_and_offers_recovery()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Services", "AgentCompatibilityService.cs"));
        var panel = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "DesktopUpdatePanel.razor"));
        var updater = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Services", "DesktopUpdateService.cs"));

        Assert.Contains("mcpTools", service, StringComparison.Ordinal);
        Assert.Contains("revision", service, StringComparison.Ordinal);
        Assert.Contains("memory_search", service, StringComparison.Ordinal);
        Assert.Contains("does not expose the capability handshake", service, StringComparison.Ordinal);
        Assert.Contains("Companion / Agent compatibility", panel, StringComparison.Ordinal);
        Assert.Contains("Restart Agent", panel, StringComparison.Ordinal);
        Assert.Contains("Check again", panel, StringComparison.Ordinal);
        Assert.Contains("AgentProcess.RestartAsync", panel, StringComparison.Ordinal);

        Assert.Contains("install-desktop-macos.sh\" --no-start", updater, StringComparison.Ordinal);
        Assert.Contains("install-desktop-windows.ps1", updater, StringComparison.Ordinal);
        Assert.Contains("launchctl kickstart -k", updater, StringComparison.Ordinal);
        Assert.Contains("start-agent-hidden.vbs", updater, StringComparison.Ordinal);
        Assert.Contains("open \"$COMPANION\"", updater, StringComparison.Ordinal);
        Assert.Contains("Start-Process -FilePath $Companion", updater, StringComparison.Ordinal);
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
