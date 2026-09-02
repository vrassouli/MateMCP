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
    public void Platform_installers_support_normal_and_elevated_background_modes()
    {
        var root = FindRepositoryRoot();
        var mac = File.ReadAllText(Path.Combine(root, "scripts", "install-macos.sh"));
        var macMode = File.ReadAllText(Path.Combine(root, "scripts", "configure-agent-mode-macos.sh"));
        var windows = File.ReadAllText(Path.Combine(root, "scripts", "install-windows.ps1"));
        var windowsMode = File.ReadAllText(Path.Combine(root, "scripts", "configure-agent-mode-windows.ps1"));

        Assert.Contains("configure-agent-mode-macos.sh", mac, StringComparison.Ordinal);
        Assert.Contains("AGENT_MODE", mac, StringComparison.Ordinal);
        Assert.Contains("/Library/LaunchDaemons/com.matemcp.agent.plist", macMode, StringComparison.Ordinal);
        Assert.Contains("launchctl bootstrap system", macMode, StringComparison.Ordinal);
        Assert.Contains("launchctl asuser", macMode, StringComparison.Ordinal);
        Assert.Contains("MATEMCP_MAC_USER_HOME", macMode, StringComparison.Ordinal);

        Assert.Contains("configure-agent-mode-windows.ps1", windows, StringComparison.Ordinal);
        Assert.Contains("& $ConfigureMode -Mode $AgentMode -NoStart", windows, StringComparison.Ordinal);
        Assert.Contains("& $ConfigureMode -Mode $AgentMode", windows, StringComparison.Ordinal);
        Assert.Contains("CreateShortcut($StartupShortcut)", windowsMode, StringComparison.Ordinal);
        Assert.Contains("New-ScheduledTaskPrincipal", windowsMode, StringComparison.Ordinal);
        Assert.Contains("-RunLevel Highest", windowsMode, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Process -FilePath $Exe", windows, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_mode_is_preserved_across_upgrade_and_elevated_startup_is_removed_on_uninstall()
    {
        var root = FindRepositoryRoot();
        var windows = File.ReadAllText(Path.Combine(root, "scripts", "install-windows.ps1"));
        var windowsDesktop = File.ReadAllText(Path.Combine(root, "scripts", "install-desktop-windows.ps1"));
        var windowsUninstall = File.ReadAllText(Path.Combine(root, "scripts", "uninstall-windows.ps1"));
        var mac = File.ReadAllText(Path.Combine(root, "scripts", "install-macos.sh"));
        var macDesktop = File.ReadAllText(Path.Combine(root, "scripts", "install-desktop-macos.sh"));
        var macUninstall = File.ReadAllText(Path.Combine(root, "scripts", "uninstall-macos.sh"));

        Assert.Contains("agent-run-mode.txt", windows, StringComparison.Ordinal);
        Assert.Contains("$persistedMode", windows, StringComparison.Ordinal);
        Assert.Contains("$persistedMode", windowsDesktop, StringComparison.Ordinal);
        Assert.Contains("Unregister-ScheduledTask", windowsUninstall, StringComparison.Ordinal);

        Assert.Contains("agent-run-mode.txt", mac, StringComparison.Ordinal);
        Assert.Contains("agent-run-mode.txt", macDesktop, StringComparison.Ordinal);
        Assert.Contains("/Library/LaunchDaemons", macUninstall, StringComparison.Ordinal);
        Assert.Contains("launchctl bootout", macUninstall, StringComparison.Ordinal);
    }

    [Fact]
    public void Elevated_macos_agent_delegates_user_data_and_keychain_back_to_signed_in_user()
    {
        var root = FindRepositoryRoot();
        var config = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Configuration", "ConfigurationBootstrap.cs"));
        var secrets = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Security", "UserSecretStore.cs"));

        Assert.Contains("MATEMCP_MAC_USER_HOME", config, StringComparison.Ordinal);
        Assert.Contains("TryRestoreDelegatedMacOwnership", config, StringComparison.Ordinal);
        Assert.Contains("MATEMCP_MAC_USER_NAME", secrets, StringComparison.Ordinal);
        Assert.Contains("MATEMCP_MAC_USER_UID", secrets, StringComparison.Ordinal);
        Assert.Contains("/bin/launchctl", secrets, StringComparison.Ordinal);
        Assert.Contains("asuser", secrets, StringComparison.Ordinal);
        Assert.Contains("/usr/bin/security", secrets, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_agent_upgrade_preserves_companion_until_companion_installer_replaces_it()
    {
        var root = FindRepositoryRoot();
        var windows = File.ReadAllText(Path.Combine(root, "scripts", "install-windows.ps1"));
        var desktop = File.ReadAllText(Path.Combine(root, "scripts", "install-desktop-windows.ps1"));

        Assert.Contains("'Companion'", windows, StringComparison.Ordinal);
        Assert.Contains("$PreserveNames", windows, StringComparison.Ordinal);
        Assert.Contains("& $AgentInstaller -Source $AgentPayload -NoStart -AgentOnly -AgentMode $AgentMode", desktop, StringComparison.Ordinal);
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
