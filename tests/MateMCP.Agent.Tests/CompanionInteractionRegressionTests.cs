namespace MateMCP.Agent.Tests;

public sealed class CompanionInteractionRegressionTests
{
    [Fact]
    public void Companion_navigation_uses_zero_hidden_badges_for_approval_and_active_shell_counts()
    {
        var main = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "MateMCP.Agent.Companion", "Components", "Main.razor"));

        Assert.Contains("private int ActiveShellCount => ShellSessions.Count(x => !x.Exited);", main, StringComparison.Ordinal);
        Assert.Contains("@if (Approvals.Count > 0)", main, StringComparison.Ordinal);
        Assert.Contains("<Badge Text=\"@Approvals.Count.ToString()\" />", main, StringComparison.Ordinal);
        Assert.Contains("@if (ActiveShellCount > 0)", main, StringComparison.Ordinal);
        Assert.Contains("<Badge Text=\"@ActiveShellCount.ToString()\" />", main, StringComparison.Ordinal);
        Assert.DoesNotContain("Approvals (@Approvals.Count)", main, StringComparison.Ordinal);
        Assert.DoesNotContain("Shell@(ActiveShellCount", main, StringComparison.Ordinal);
    }

    [Fact]
    public void Companion_overview_surfaces_attention_usage_updates_and_mcp_connection()
    {
        var root = FindRepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "Main.razor"));
        var updateOverview = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "DesktopUpdateOverviewCard.razor"));
        var updatePanel = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "DesktopUpdatePanel.razor"));

        Assert.Contains("Current usage", main, StringComparison.Ordinal);
        Assert.Contains("Prevent sleep while in use", main, StringComparison.Ordinal);
        Assert.Contains("<DesktopUpdateOverviewCard />", main, StringComparison.Ordinal);
        Assert.Contains("Device MCP URL", main, StringComparison.Ordinal);
        Assert.Contains("Copy MCP URL", main, StringComparison.Ordinal);
        Assert.Contains("Review approvals", main, StringComparison.Ordinal);
        Assert.DoesNotContain("Credential store", main, StringComparison.Ordinal);
        Assert.Contains("Update available", updateOverview, StringComparison.Ordinal);
        Assert.Contains("Update now", updateOverview, StringComparison.Ordinal);
        Assert.DoesNotContain("Agent activity", updatePanel, StringComparison.Ordinal);
        Assert.DoesNotContain("Prevent Sleep While In Use", updatePanel, StringComparison.Ordinal);
        var styles = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "css", "app.css"));
        Assert.Matches(@"(?s)\.metric\s*\{[^}]*line-height:\s*1\.2;", styles);
    }

    [Fact]
    public void Companion_webview_supports_tab_and_shift_tab_focus_traversal()
    {
        var index = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "MateMCP.Agent.Companion", "wwwroot", "index.html"));

        Assert.Contains("event.key !== 'Tab'", index, StringComparison.Ordinal);
        Assert.Contains("event.shiftKey", index, StringComparison.Ordinal);
        Assert.Contains("focusable[next].focus()", index, StringComparison.Ordinal);
        Assert.Contains("event.preventDefault()", index, StringComparison.Ordinal);
    }

    [Fact]
    public void Companion_live_preview_can_collapse_and_does_not_overlay_narrow_controls()
    {
        var root = FindRepositoryRoot();
        var preview = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "ComputerUsePreviewCard.razor"));
        var styles = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "css", "app.css"));

        Assert.Contains("aria-controls=\"computer-use-preview-body\"", preview, StringComparison.Ordinal);
        Assert.Contains("aria-expanded=\"@(!Collapsed)\"", preview, StringComparison.Ordinal);
        Assert.Contains("private void ToggleCollapsed()", preview, StringComparison.Ordinal);
        Assert.Contains("if (Collapsed || IsSelfPreview)", preview, StringComparison.Ordinal);
        Assert.Contains("avoid recursive self-preview", preview, StringComparison.Ordinal);
        Assert.Matches(@"(?s)@media \(max-width: 760px\).*?\.computer-use-preview-card\s*\{[^}]*position:\s*static;", styles);
    }

    [Fact]
    public void Companion_terminal_follow_pauses_when_user_scrolls_up_and_resumes_at_bottom()
    {
        var index = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "MateMCP.Agent.Companion", "wwwroot", "index.html"));

        Assert.Contains("const nearBottom", index, StringComparison.Ordinal);
        Assert.Contains("follow = nearBottom()", index, StringComparison.Ordinal);
        Assert.Contains("if (!follow) return", index, StringComparison.Ordinal);
        Assert.Contains("terminal.scrollTop = terminal.scrollHeight", index, StringComparison.Ordinal);
        Assert.Contains("new MutationObserver", index, StringComparison.Ordinal);
    }

    [Fact]
    public void Companion_devices_keeps_local_identity_visible_when_control_plane_is_unavailable()
    {
        var panel = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "MateMCP.Agent.Companion", "Components", "DevicesPanel.razor"));
        var service = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "MateMCP.Agent", "Relay", "DeviceManagementService.cs"));

        Assert.Contains("Status.UpstreamError", panel, StringComparison.Ordinal);
        Assert.Contains("Local device identity", panel, StringComparison.Ordinal);
        Assert.Contains("if (!response.IsSuccessStatusCode)", service, StringComparison.Ordinal);
        Assert.Contains("new DeviceManagementStatus(true", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Companion_activity_defaults_to_a_date_range_and_requires_cleanup_confirmation()
    {
        var main = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "MateMCP.Agent.Companion", "Components", "Main.razor"));
        var client = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "MateMCP.Agent.Companion", "Services", "AgentApiClient.cs"));

        Assert.Contains("DateOnly.FromDateTime(DateTime.Now)", main, StringComparison.Ordinal);
        Assert.Contains("type=\"date\"", main, StringComparison.Ordinal);
        Assert.Contains("ConfirmAuditCleanup", main, StringComparison.Ordinal);
        Assert.Contains("Api.CleanupAuditAsync", main, StringComparison.Ordinal);
        Assert.Contains("&from=", client, StringComparison.Ordinal);
        Assert.Contains("&to=", client, StringComparison.Ordinal);
    }

    [Fact]
    public void Companion_visible_form_labels_have_explicit_control_associations()
    {
        var root = FindRepositoryRoot();
        var projects = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "ProjectsPanel.razor"));
        var logs = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "AgentLogsPanel.razor"));
        var memory = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "SkillsMemoryPanel.razor"));
        var main = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "Main.razor"));

        Assert.Contains("<label for=\"project-name\">Name</label><input id=\"project-name\"", projects, StringComparison.Ordinal);
        Assert.Contains("<label for=\"project-workspace-path\">Workspace path</label><input id=\"project-workspace-path\"", projects, StringComparison.Ordinal);
        Assert.Contains("<label for=\"agent-log-level\">Minimum level</label>", logs, StringComparison.Ordinal);
        Assert.Contains("<select id=\"agent-log-level\"", logs, StringComparison.Ordinal);
        Assert.Contains("<label for=\"agent-log-search\">Search</label>", logs, StringComparison.Ordinal);
        Assert.Contains("<input id=\"agent-log-search\"", logs, StringComparison.Ordinal);
        Assert.Contains("<label for=\"memory-title\">Title</label><input id=\"memory-title\"", memory, StringComparison.Ordinal);
        Assert.Contains("<label for=\"memory-content\">Content (Markdown)</label><textarea id=\"memory-content\"", memory, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Filter by type\"", memory, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Search Skills and Memory\"", memory, StringComparison.Ordinal);
        Assert.Contains("<label for=\"shell-input\">Input</label>", main, StringComparison.Ordinal);
        Assert.Contains("<input id=\"shell-input\"", main, StringComparison.Ordinal);
        Assert.Contains("<label for=\"secret-name\">Name</label><input id=\"secret-name\"", main, StringComparison.Ordinal);
        Assert.Contains("<label for=\"secret-value\">Secret value</label><input id=\"secret-value\"", main, StringComparison.Ordinal);
        Assert.Contains("role=\"group\" aria-labelledby=\"secret-allowed-use-label\"", main, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Activity date\"", main, StringComparison.Ordinal);
    }

    [Fact]
    public void Mac_secret_metadata_uses_stable_user_application_support_and_elevated_agent_delegates_keychain()
    {
        var store = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "MateMCP.Agent", "Security", "UserSecretStore.cs"));

        Assert.Contains("Library", store, StringComparison.Ordinal);
        Assert.Contains("Application Support", store, StringComparison.Ordinal);
        Assert.Contains("MATEMCP_MAC_USER_HOME", store, StringComparison.Ordinal);
        Assert.Contains("File.Copy(applicationDataPath, stablePath, overwrite: false)", store, StringComparison.Ordinal);
        Assert.Contains("MATEMCP_MAC_USER_NAME", store, StringComparison.Ordinal);
        Assert.Contains("MATEMCP_MAC_USER_UID", store, StringComparison.Ordinal);
        Assert.Contains("/bin/launchctl", store, StringComparison.Ordinal);
        Assert.Contains("asuser", store, StringComparison.Ordinal);
        Assert.Contains("/usr/bin/security", store, StringComparison.Ordinal);
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
