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
