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
    public void Platform_installers_start_the_agent_in_the_background_by_default()
    {
        var root = FindRepositoryRoot();
        var mac = File.ReadAllText(Path.Combine(root, "scripts", "install-macos.sh"));
        var windows = File.ReadAllText(Path.Combine(root, "scripts", "install-windows.ps1"));

        Assert.Contains("launchctl bootstrap", mac, StringComparison.Ordinal);
        Assert.Contains("launchctl kickstart", mac, StringComparison.Ordinal);

        Assert.Contains("start-agent-hidden.vbs", windows, StringComparison.Ordinal);
        Assert.Contains("CreateShortcut($StartupShortcut)", windows, StringComparison.Ordinal);
        Assert.Contains("$shortcut.TargetPath = $WScript", windows, StringComparison.Ordinal);
        Assert.Contains("$shortcut.Arguments = \"`\"$HiddenLauncher`\"\"", windows, StringComparison.Ordinal);
        Assert.Contains("Start-Process -FilePath $WScript", windows, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process -FilePath $Exe", windows, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_agent_upgrade_preserves_companion_until_companion_installer_replaces_it()
    {
        var root = FindRepositoryRoot();
        var windows = File.ReadAllText(Path.Combine(root, "scripts", "install-windows.ps1"));
        var desktop = File.ReadAllText(Path.Combine(root, "scripts", "install-desktop-windows.ps1"));

        Assert.Contains("'Companion'", windows, StringComparison.Ordinal);
        Assert.Contains("$PreserveNames", windows, StringComparison.Ordinal);
        Assert.Contains("& $AgentInstaller -Source $AgentPayload -NoStart -AgentOnly", desktop, StringComparison.Ordinal);
        Assert.Contains("& $CompanionInstaller -Source $CompanionPayload -NoStart", desktop, StringComparison.Ordinal);
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
