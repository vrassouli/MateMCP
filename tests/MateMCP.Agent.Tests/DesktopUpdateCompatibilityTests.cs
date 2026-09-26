namespace MateMCP.Agent.Tests;

public sealed class DesktopUpdateCompatibilityTests
{
    [Fact]
    public void Agent_exposes_management_api_handshake_and_skills_memory_routes()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Program.cs"));

        Assert.Contains("version = agentVersion", program, StringComparison.Ordinal);
        Assert.Contains("managementApi = new", program, StringComparison.Ordinal);
        Assert.Contains("skills-memory", program, StringComparison.Ordinal);
        Assert.Contains("global-skills-memory", program, StringComparison.Ordinal);
        Assert.Contains("repository-skills", program, StringComparison.Ordinal);
        Assert.Contains("projects-stable-id", program, StringComparison.Ordinal);
        Assert.Contains("app.MapGet(\"/skills-memory\"", program, StringComparison.Ordinal);
        Assert.Contains("app.MapPost(\"/skills-memory\"", program, StringComparison.Ordinal);
        Assert.Contains("app.MapPut(\"/skills-memory/{id}\"", program, StringComparison.Ordinal);
        Assert.Contains("app.MapDelete(\"/skills-memory/{id}\"", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Companion_verifies_management_endpoints_not_only_mcp_tool_names()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Services", "AgentCompatibilityService.cs"));
        var panel = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "DesktopUpdatePanel.razor"));

        Assert.Contains("mcpTools", service, StringComparison.Ordinal);
        Assert.Contains("managementApi", service, StringComparison.Ordinal);
        Assert.Contains("RequiredManagementCapabilities", service, StringComparison.Ordinal);
        Assert.Contains("global-skills-memory", service, StringComparison.Ordinal);
        Assert.Contains("repository-skills", service, StringComparison.Ordinal);
        Assert.Contains("skills-memory?includeDisabled=true", service, StringComparison.Ordinal);
        Assert.Contains("\"projects\"", service, StringComparison.Ordinal);
        Assert.Contains("\"desktop-update\"", service, StringComparison.Ordinal);
        Assert.Contains("\"logs?limit=1\"", service, StringComparison.Ordinal);
        Assert.Contains("exposes MCP memory tools but not the local management API", service, StringComparison.Ordinal);
        Assert.Contains("AgentVersion", panel, StringComparison.Ordinal);
        Assert.Contains("ManagementApiRevision", panel, StringComparison.Ordinal);
        Assert.Contains("Repair current package", panel, StringComparison.Ordinal);
        Assert.Contains("Restart Agent", panel, StringComparison.Ordinal);
        Assert.Contains("Check again", panel, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_updater_verifies_package_and_preserves_agent_execution_mode()
    {
        var root = FindRepositoryRoot();
        var updater = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Services", "DesktopUpdateService.cs"));

        Assert.Contains("installedPackageKnown", updater, StringComparison.Ordinal);
        Assert.DoesNotContain("establish", updater, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ParseSha256Digest", updater, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.FixedTimeEquals", updater, StringComparison.Ordinal);
        Assert.Contains("agent-run-mode.txt", updater, StringComparison.Ordinal);
        Assert.Contains("--agent-mode Elevated", updater, StringComparison.Ordinal);
        Assert.Contains("configure-agent-mode-macos.sh", updater, StringComparison.Ordinal);
        Assert.Contains("/usr/bin/osascript", updater, StringComparison.Ordinal);
        Assert.Contains("schtasks.exe /Run /TN $TaskName", updater, StringComparison.Ordinal);
        Assert.Contains("install-desktop-windows.ps1", updater, StringComparison.Ordinal);
        Assert.Contains("Wait-AgentHealth", updater, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:45871/health", updater, StringComparison.Ordinal);
        Assert.Contains("StartMacLaunchdJob", updater, StringComparison.Ordinal);
        Assert.Contains("<key>KeepAlive</key><false/>", updater, StringComparison.Ordinal);
        Assert.Contains("launchctl bootout", updater, StringComparison.Ordinal);
    }

    [Fact]
    public void Background_updater_restarts_the_selected_execution_mode()
    {
        var root = FindRepositoryRoot();
        var updater = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Desktop", "BackgroundDesktopUpdateService.cs"));

        Assert.Contains("agent-run-mode.txt", updater, StringComparison.Ordinal);
        Assert.Contains("--agent-mode \"$AGENT_MODE\"", updater, StringComparison.Ordinal);
        Assert.Contains("MATEMCP_MAC_USER_HOME", updater, StringComparison.Ordinal);
        Assert.Contains("configure-agent-mode-macos.sh", updater, StringComparison.Ordinal);
        Assert.Contains("schtasks.exe /Run /TN $TaskName", updater, StringComparison.Ordinal);
        Assert.Contains("Wait-AgentHealth", updater, StringComparison.Ordinal);
        Assert.Contains("wait_agent_health", updater, StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:45871/health", updater, StringComparison.Ordinal);
        Assert.Contains("StartMacLaunchdJob", updater, StringComparison.Ordinal);
        Assert.Contains("currentUid == 0 ? \"system\"", updater, StringComparison.Ordinal);
        Assert.Contains("<key>KeepAlive</key><false/>", updater, StringComparison.Ordinal);
        Assert.Contains("launchctl bootout", updater, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_companion_upgrade_preserves_webview_user_data()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "scripts", "install-companion-windows.ps1"));

        Assert.Contains("$WebViewUserData = Join-Path $Target 'MateMCP.Agent.Companion.exe.WebView2'", installer, StringComparison.Ordinal);
        Assert.Contains("Where-Object { $_.FullName -ne $WebViewUserData }", installer, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -Recurse -Force", installer, StringComparison.Ordinal);

        var preserve = installer.IndexOf("Where-Object { $_.FullName -ne $WebViewUserData }", StringComparison.Ordinal);
        var remove = installer.IndexOf("Remove-Item -Recurse -Force", preserve, StringComparison.Ordinal);
        var copy = installer.IndexOf("Copy-Item (Join-Path $Source '*') $Target -Recurse -Force", StringComparison.Ordinal);
        Assert.True(preserve >= 0 && remove > preserve && copy > remove);
    }

    [Fact]
    public void Mac_installer_waits_for_old_agent_before_replacing_managed_payload()
    {
        var root = FindRepositoryRoot();
        var installer = File.ReadAllText(Path.Combine(root, "scripts", "install-macos.sh"));

        Assert.Contains("wait_pid_exit", installer, StringComparison.Ordinal);
        Assert.Contains("launchctl bootout", installer, StringComparison.Ordinal);
        Assert.Contains("did not stop before payload replacement", installer, StringComparison.Ordinal);
        Assert.True(installer.IndexOf("wait_pid_exit \"$GUI_PID\"", StringComparison.Ordinal)
            < installer.IndexOf("rm -rf \"$TARGET\"/*", StringComparison.Ordinal));

        var windowsInstaller = File.ReadAllText(Path.Combine(root, "scripts", "install-windows.ps1"));
        Assert.Contains("did not stop before payload replacement", windowsInstaller, StringComparison.Ordinal);
        Assert.True(windowsInstaller.IndexOf("$stopDeadline", StringComparison.Ordinal)
            < windowsInstaller.IndexOf("Get-ChildItem $Target", StringComparison.Ordinal));
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
