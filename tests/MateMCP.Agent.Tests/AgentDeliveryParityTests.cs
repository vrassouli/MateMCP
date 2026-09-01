namespace MateMCP.Agent.Tests;

public sealed class AgentDeliveryParityTests
{
    [Fact]
    public void Bootstrap_scripts_use_the_same_stable_release_channel()
    {
        var root = FindRepositoryRoot();
        var mac = File.ReadAllText(Path.Combine(root, "scripts", "bootstrap-macos.sh"));
        var windows = File.ReadAllText(Path.Combine(root, "scripts", "bootstrap-windows.ps1"));

        Assert.Contains("agent-latest", mac, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-dev", mac, StringComparison.Ordinal);
        Assert.Contains("agent-latest", windows, StringComparison.Ordinal);
    }

    [Fact]
    public void Platform_installers_start_the_agent_by_default()
    {
        var root = FindRepositoryRoot();
        var mac = File.ReadAllText(Path.Combine(root, "scripts", "install-macos.sh"));
        var windows = File.ReadAllText(Path.Combine(root, "scripts", "install-windows.ps1"));

        Assert.Contains("launchctl bootstrap", mac, StringComparison.Ordinal);
        Assert.Contains("launchctl kickstart", mac, StringComparison.Ordinal);
        Assert.Contains("Start-Process -FilePath $Exe", windows, StringComparison.Ordinal);
        Assert.Contains("CreateShortcut($StartupShortcut)", windows, StringComparison.Ordinal);
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
