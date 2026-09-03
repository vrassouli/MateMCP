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
    public void Companion_webview_supports_tab_and_shift_tab_focus_traversal()
    {
        var index = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "MateMCP.Agent.Companion", "wwwroot", "index.html"));

        Assert.Contains("event.key !== 'Tab'", index, StringComparison.Ordinal);
        Assert.Contains("event.shiftKey", index, StringComparison.Ordinal);
        Assert.Contains("focusable[next].focus()", index, StringComparison.Ordinal);
        Assert.Contains("event.preventDefault()", index, StringComparison.Ordinal);
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
