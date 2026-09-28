namespace MateMCP.Agent.Tests;

public sealed class CompanionInteractionRegressionTests
{
    [Fact]
    public void Companion_navigation_uses_zero_hidden_badges_for_approval_and_active_shell_counts()
    {
        var root = FindRepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "Main.razor"));
        var navigation = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "CompanionNavigation.razor"));

        Assert.Contains("private int ActiveShellCount => ShellSessions.Count(x => !x.Exited);", main, StringComparison.Ordinal);
        Assert.Contains("@if (ApprovalsCount > 0)", navigation, StringComparison.Ordinal);
        Assert.Contains("<Badge Text=\"@ApprovalsCount.ToString()\" />", navigation, StringComparison.Ordinal);
        Assert.Contains("@if (ActiveShellCount > 0)", navigation, StringComparison.Ordinal);
        Assert.Contains("<Badge Text=\"@ActiveShellCount.ToString()\" />", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("Approvals (@ApprovalsCount)", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("Shell@(ActiveShellCount", navigation, StringComparison.Ordinal);
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
        var root = FindRepositoryRoot();
        var index = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "index.html"));
        var catalystApplication = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Platforms", "MacCatalyst", "MateMcpApplication.cs"));
        var catalystBridge = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Platforms", "MacCatalyst", "MateMcpNativeTabBridge.cs"));

        Assert.Contains("event.key !== 'Tab'", index, StringComparison.Ordinal);
        Assert.Contains("event.shiftKey", index, StringComparison.Ordinal);
        Assert.Contains("focusable[next].focus()", index, StringComparison.Ordinal);
        Assert.Contains("event.preventDefault()", index, StringComparison.Ordinal);
        Assert.Contains("mateMcpTabForward:", catalystApplication, StringComparison.Ordinal);
        Assert.Contains("mateMcpTabBackward:", catalystApplication, StringComparison.Ordinal);
        Assert.DoesNotContain("new Selector(\"insertTab:\")", catalystApplication, StringComparison.Ordinal);
        Assert.DoesNotContain("new Selector(\"insertBacktab:\")", catalystApplication, StringComparison.Ordinal);
        Assert.Contains("public override void SendEvent(UIEvent uievent)", catalystApplication, StringComparison.Ordinal);
        Assert.Contains("uievent is UIPressesEvent", catalystApplication, StringComparison.Ordinal);
        Assert.Contains("private const string BackTabCharacter = \"\\u0019\"", catalystApplication, StringComparison.Ordinal);
        Assert.Contains("characters is not TabCharacter and not BackTabCharacter", catalystApplication, StringComparison.Ordinal);
        Assert.Contains("characters == BackTabCharacter ||", catalystApplication, StringComparison.Ordinal);
        Assert.Contains("press.Phase == UIPressPhase.Began", catalystApplication, StringComparison.Ordinal);
        Assert.Contains("UIKeyModifierFlags.Shift", catalystApplication, StringComparison.Ordinal);
        Assert.Contains("base.SendEvent(uievent)", catalystApplication, StringComparison.Ordinal);
        Assert.Contains("MateMcpNativeTabBridge.AdvanceFocus", catalystApplication, StringComparison.Ordinal);
        Assert.Contains("key.KeyCode != UIKeyboardHidUsage.KeyboardEscape", catalystApplication, StringComparison.Ordinal);
        Assert.Contains("ForwardEscapeToDom(pressesEvent);", catalystApplication, StringComparison.Ordinal);
        Assert.Contains("MateMcpNativeTabBridge.CloseTopDialog();", catalystApplication, StringComparison.Ordinal);
        Assert.Contains("CreatePriorityCommand", catalystBridge, StringComparison.Ordinal);
        Assert.Contains("WantsPriorityOverSystemBehavior = true", catalystBridge, StringComparison.Ordinal);
        Assert.Contains("controller.AddKeyCommand(_controllerForwardCommand)", catalystBridge, StringComparison.Ordinal);
        Assert.Contains("controller.AddKeyCommand(_controllerBackwardCommand)", catalystBridge, StringComparison.Ordinal);
        Assert.Contains("window.mateMcpFocus.advance", catalystBridge, StringComparison.Ordinal);
        Assert.Contains("EvaluateJavaScript", catalystBridge, StringComparison.Ordinal);
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
    public void Companion_checkboxes_use_bluent_without_global_native_overrides()
    {
        var root = FindRepositoryRoot();
        var componentsDirectory = Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components");
        var styles = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "css", "app.css"));

        Assert.Contains("<Checkbox TValue=\"bool\"", File.ReadAllText(Path.Combine(componentsDirectory, "Main.razor")), StringComparison.Ordinal);
        Assert.Contains("<Checkbox TValue=\"bool\"", File.ReadAllText(Path.Combine(componentsDirectory, "ProjectsPanel.razor")), StringComparison.Ordinal);
        Assert.Contains("<Checkbox TValue=\"bool\"", File.ReadAllText(Path.Combine(componentsDirectory, "AgentLogsPanel.razor")), StringComparison.Ordinal);
        Assert.Contains("<Checkbox TValue=\"bool\"", File.ReadAllText(Path.Combine(componentsDirectory, "DesktopUpdatePanel.razor")), StringComparison.Ordinal);
        Assert.DoesNotContain("input[type=\"checkbox\"] {", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".update-toggle input", styles, StringComparison.Ordinal);
    }
    [Fact]
    public void Companion_narrow_navigation_uses_a_modal_drawer_and_scopes_focus_to_it()
    {
        var root = FindRepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "Main.razor"));
        var navigation = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "CompanionNavigation.razor"));
        var index = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "index.html"));
        var styles = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "css", "app.css"));

        Assert.Equal(2, main.Split("<CompanionNavigation", StringSplitOptions.None).Length - 1);
        Assert.Contains("id=\"companion-nav-menu-button\"", main, StringComparison.Ordinal);
        Assert.Contains("<dialog id=\"companion-nav-drawer\"", main, StringComparison.Ordinal);
        Assert.Contains("aria-controls=\"companion-nav-drawer\"", main, StringComparison.Ordinal);
        Assert.Contains("OnNavigate=\"NavigateFromDrawerAsync\"", main, StringComparison.Ordinal);
        Assert.Contains("EventCallback<string> OnNavigate", navigation, StringComparison.Ordinal);

        Assert.Contains("dialog.showModal()", index, StringComparison.Ordinal);
        Assert.Contains("dialog.addEventListener('close'", index, StringComparison.Ordinal);
        Assert.Contains("opener.focus()", index, StringComparison.Ordinal);
        Assert.Contains("const root = openDialogs.length ? openDialogs[openDialogs.length - 1] : document;", index, StringComparison.Ordinal);
        Assert.Contains("root.querySelectorAll(selector)", index, StringComparison.Ordinal);

        Assert.Matches(@"(?s)@media \(max-width: 760px\).*?\.sidebar\s*\{\s*display:\s*none;", styles);
        Assert.Contains(".mobile-nav-trigger { display: grid; }", styles, StringComparison.Ordinal);
        Assert.Contains(":dir(rtl) .nav-drawer-panel", styles, StringComparison.Ordinal);

    }

    [Fact]
    public void Companion_header_stays_outside_content_scroll_and_managed_pages_use_inner_scroll_owners()
    {
        var root = FindRepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "Main.razor"));
        var styles = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "css", "app.css"));

        var headerIndex = main.IndexOf("<header class=\"content-header\">", StringComparison.Ordinal);
        var scrollIndex = main.IndexOf("<div class=\"@ContentScrollClass\">", StringComparison.Ordinal);
        Assert.True(headerIndex >= 0 && scrollIndex > headerIndex);
        Assert.Contains("Section is \"dashboard\" or \"logs\" or \"audit\"", main, StringComparison.Ordinal);
        Assert.Contains("content-scroll content-scroll-managed", main, StringComparison.Ordinal);
        Assert.Matches(@"(?s)\.content\s*\{[^}]*grid-template-rows:\s*auto minmax\(0, 1fr\);[^}]*overflow:\s*hidden;", styles);
        Assert.Matches(@"(?s)\.content-scroll\s*\{[^}]*overflow-y:\s*auto;[^}]*scroll-padding-block:\s*16px;", styles);
        Assert.Matches(@"(?s)\.content-scroll-managed\s*\{[^}]*display:\s*flex;[^}]*overflow:\s*hidden;", styles);
        Assert.Contains(".dashboard-tabs > .panels > .tab-panel", styles, StringComparison.Ordinal);
        Assert.Contains(".audit-results {", styles, StringComparison.Ordinal);
        Assert.Contains(".agent-logs-panel {", styles, StringComparison.Ordinal);
        Assert.Contains("html, body { overflow: hidden; }", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Companion_navigation_resets_the_shared_content_scroll_region()
    {
        var root = FindRepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "Main.razor"));
        var index = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "index.html"));

        Assert.Contains("mateMcpLayout.resetContentScroll", main, StringComparison.Ordinal);
        Assert.Contains("await NavigateAsync(section);", main, StringComparison.Ordinal);
        Assert.Contains("NavigateAsync(\"approvals\")", main, StringComparison.Ordinal);
        Assert.Contains("NavigateAsync(\"shell\")", main, StringComparison.Ordinal);
        Assert.Contains("window.mateMcpLayout", index, StringComparison.Ordinal);
        Assert.Contains("document.querySelector('.content-scroll')?.scrollTo", index, StringComparison.Ordinal);
    }

    [Fact]
    public void Companion_interactive_shell_hides_ansi_vt_control_sequences()
    {
        const string raw = "\u001b[1t\u001b[?1004h\u001b[?9001h(root@192.168.200.34) Password: \u001b[2;1Hecho READY; uname -a\u001b[2;21H\u001b[2;21H READY FreeBSD OPNsense.internal";
        var plain = MateMCP.Agent.Companion.Services.TerminalOutputSanitizer.ToPlainText(raw);

        Assert.DoesNotContain('\u001b', plain);
        Assert.DoesNotContain("[?1004h", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("[2;1H", plain, StringComparison.Ordinal);
        Assert.Contains("(root@192.168.200.34) Password: ", plain, StringComparison.Ordinal);
        Assert.Contains("echo READY; uname -a", plain, StringComparison.Ordinal);
        Assert.Contains("READY FreeBSD OPNsense.internal", plain, StringComparison.Ordinal);

        const string styled = "\u001b]0;server-title\u0007ready \u001b[31mred\u001b[0m";
        Assert.Equal("ready red", MateMCP.Agent.Companion.Services.TerminalOutputSanitizer.ToPlainText(styled));

        var main = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "MateMCP.Agent.Companion", "Components", "Main.razor"));
        Assert.Contains("@TerminalOutputSanitizer.ToPlainText(ShellOutput)", main, StringComparison.Ordinal);
        Assert.DoesNotContain("<div class=\"terminal\">@ShellOutput</div>", main, StringComparison.Ordinal);
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
    public void Companion_devices_stops_loading_and_polling_before_disposal_state_updates()
    {
        var panel = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "MateMCP.Agent.Companion", "Components", "DevicesPanel.razor"));

        Assert.Contains("var ct = _disposeCts.Token;", panel, StringComparison.Ordinal);
        Assert.Contains("await RefreshAsync(ct);", panel, StringComparison.Ordinal);
        Assert.Contains("if (_disposed || ct.IsCancellationRequested) return;", panel, StringComparison.Ordinal);
        Assert.Contains("private async Task RefreshAsync(CancellationToken ct)", panel, StringComparison.Ordinal);
        Assert.Contains("if (!_disposed && !ct.IsCancellationRequested)", panel, StringComparison.Ordinal);
        Assert.Contains("StateHasChanged();", panel, StringComparison.Ordinal);
        Assert.Contains("catch (ObjectDisposedException) when (_disposed || ct.IsCancellationRequested)", panel, StringComparison.Ordinal);
        Assert.Contains("catch (InvalidOperationException) when (_disposed || ct.IsCancellationRequested)", panel, StringComparison.Ordinal);

        var disposedIndex = panel.IndexOf("_disposed = true;", StringComparison.Ordinal);
        var cancelIndex = panel.IndexOf("_disposeCts.Cancel();", StringComparison.Ordinal);
        Assert.True(disposedIndex >= 0 && cancelIndex > disposedIndex);
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
    public void Companion_activity_filters_at_storage_layer_and_live_refreshes_only_while_active()
    {
        var root = FindRepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "Main.razor"));
        var client = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Services", "AgentApiClient.cs"));
        var styles = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "css", "app.css"));

        Assert.Contains("DateOnly.FromDateTime(DateTime.Now)", main, StringComparison.Ordinal);
        Assert.Contains("<DateField TValue=\"DateOnly\" id=\"audit-date\"", main, StringComparison.Ordinal);
        Assert.Contains("<SelectField TValue=\"string\" id=\"audit-project-filter\"", main, StringComparison.Ordinal);
        Assert.Contains("id=\"audit-project-filter\"", main, StringComparison.Ordinal);
        Assert.Contains("id=\"audit-capability-filter\"", main, StringComparison.Ordinal);
        Assert.Contains("ClearAuditFiltersAsync", main, StringComparison.Ordinal);
        Assert.Contains("AuditLiveRefresh", main, StringComparison.Ordinal);
        Assert.Contains("Section == \"audit\"", main, StringComparison.Ordinal);
        Assert.Contains("_auditPollTicks >= 5", main, StringComparison.Ordinal);
        Assert.Contains("AuditDate == DateOnly.FromDateTime(DateTime.Now)", main, StringComparison.Ordinal);
        Assert.Contains("No activity matches the current filters.", main, StringComparison.Ordinal);
        Assert.Contains("project: @item.Project", main, StringComparison.Ordinal);
        Assert.Contains("ConfirmAuditCleanup", main, StringComparison.Ordinal);
        Assert.Contains("Api.CleanupAuditAsync", main, StringComparison.Ordinal);

        Assert.Contains("&from=", client, StringComparison.Ordinal);
        Assert.Contains("&to=", client, StringComparison.Ordinal);
        Assert.Contains("&project=", client, StringComparison.Ordinal);
        Assert.Contains("&capability=", client, StringComparison.Ordinal);
        Assert.Contains(".audit-filters {", styles, StringComparison.Ordinal);
        Assert.Contains(".audit-date-nav {", styles, StringComparison.Ordinal);
        Assert.Matches(@"(?s)\.audit-toolbar\s*\{[^}]*flex-wrap:\s*wrap;", styles);
        Assert.Matches(@"(?s)\.audit-date-nav\s*\{[^}]*flex:\s*0 0 auto;[^}]*flex-wrap:\s*nowrap;[^}]*max-width:\s*100%;", styles);
        Assert.Contains(".audit-results {", styles, StringComparison.Ordinal);
        Assert.Matches(@"(?s)\.audit-results\s*\{[^}]*overflow-y:\s*auto;", styles);
        Assert.Matches(@"(?s)@media \(max-width: 760px\).*?\.audit-filters\s*\{\s*grid-template-columns:\s*1fr;", styles);
        Assert.Contains("class=\"row audit-row\"", main, StringComparison.Ordinal);
        Assert.Contains(".audit-row {", styles, StringComparison.Ordinal);
        Assert.Contains(".audit-row > * {", styles, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere;", styles, StringComparison.Ordinal);
        Assert.Contains("word-break: break-word;", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Companion_visible_form_labels_have_explicit_control_associations()
    {
        var root = FindRepositoryRoot();
        var projects = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "ProjectsPanel.razor"));
        var logs = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "AgentLogsPanel.razor"));
        var memory = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "SkillsMemoryPanel.razor"));
        var main = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "Main.razor"));

        Assert.Contains("<label for=\"project-name\">Name</label><TextField id=\"project-name\"", projects, StringComparison.Ordinal);
        Assert.Contains("<label for=\"project-workspace-path\">Workspace path</label>", projects, StringComparison.Ordinal);
        Assert.Contains("<TextField id=\"project-workspace-path\"", projects, StringComparison.Ordinal);
        Assert.Contains("<label for=\"agent-log-level\">Minimum level</label>", logs, StringComparison.Ordinal);
        Assert.Contains("<SelectField TValue=\"string\" id=\"agent-log-level\"", logs, StringComparison.Ordinal);
        Assert.Contains("<label for=\"agent-log-search\">Search</label>", logs, StringComparison.Ordinal);
        Assert.Contains("<TextField id=\"agent-log-search\"", logs, StringComparison.Ordinal);
        Assert.Contains("<label for=\"memory-title\">Title</label><TextField id=\"memory-title\"", memory, StringComparison.Ordinal);
        Assert.Contains("<label for=\"memory-content\">Content (Markdown)</label><TextField id=\"memory-content\" Rows=\"12\"", memory, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Filter by type\"", memory, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Search Skills and Memory\"", memory, StringComparison.Ordinal);
        Assert.Contains("<label for=\"shell-input\">Input</label>", main, StringComparison.Ordinal);
        Assert.Contains("<TextField id=\"shell-input\"", main, StringComparison.Ordinal);
        Assert.Contains("<label for=\"secret-name\">Name</label><TextField id=\"secret-name\"", main, StringComparison.Ordinal);
        Assert.Contains("<label for=\"secret-value\">Secret value</label><TextField id=\"secret-value\"", main, StringComparison.Ordinal);
        Assert.Contains("role=\"group\" aria-labelledby=\"secret-allowed-use-label\"", main, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Activity date\"", main, StringComparison.Ordinal);
        Assert.Contains("<label for=\"audit-project-filter\">Project</label>", main, StringComparison.Ordinal);
        Assert.Contains("<label for=\"audit-capability-filter\">Action / capability</label>", main, StringComparison.Ordinal);
    }

    [Fact]
    public void Companion_managed_scroll_pages_use_bluent_controls_and_fill_available_height()
    {
        var root = FindRepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "Main.razor"));
        var logs = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "AgentLogsPanel.razor"));
        var styles = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "css", "app.css"));

        Assert.Contains("<TabList Class=\"dashboard-tabs\">", main, StringComparison.Ordinal);
        Assert.Contains("<SelectField TValue=\"string\" id=\"audit-project-filter\"", main, StringComparison.Ordinal);
        Assert.Contains("<DateField TValue=\"DateOnly\" id=\"audit-date\"", main, StringComparison.Ordinal);
        Assert.Contains("<TextField id=\"audit-capability-filter\"", main, StringComparison.Ordinal);
        Assert.Contains("<TextField id=\"shell-input\"", main, StringComparison.Ordinal);
        Assert.Contains("<Checkbox TValue=\"bool\"", main, StringComparison.Ordinal);
        Assert.DoesNotContain("<input", main, StringComparison.Ordinal);
        Assert.DoesNotContain("<select", main, StringComparison.Ordinal);
        Assert.DoesNotContain("<textarea", main, StringComparison.Ordinal);

        Assert.Contains("class=\"panel agent-logs-panel\"", logs, StringComparison.Ordinal);
        Assert.Contains("<SelectField TValue=\"string\" id=\"agent-log-level\"", logs, StringComparison.Ordinal);
        Assert.Contains("<TextField id=\"agent-log-search\"", logs, StringComparison.Ordinal);
        Assert.Contains("<Checkbox TValue=\"bool\" @bind-Value=\"Live\"", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("<input", logs, StringComparison.Ordinal);
        Assert.DoesNotContain("<select", logs, StringComparison.Ordinal);

        var componentsDirectory = Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components");
        foreach (var componentPath in Directory.EnumerateFiles(componentsDirectory, "*.razor", SearchOption.AllDirectories))
        {
            var markup = File.ReadAllText(componentPath);
            Assert.DoesNotContain("<input", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("<select", markup, StringComparison.Ordinal);
            Assert.DoesNotContain("<textarea", markup, StringComparison.Ordinal);
        }

        var projects = File.ReadAllText(Path.Combine(componentsDirectory, "ProjectsPanel.razor"));
        var memory = File.ReadAllText(Path.Combine(componentsDirectory, "SkillsMemoryPanel.razor"));
        var updates = File.ReadAllText(Path.Combine(componentsDirectory, "DesktopUpdatePanel.razor"));
        Assert.Contains("<TextField BindValueEvent=\"oninput\" @bind-Value=\"Search\"", projects, StringComparison.Ordinal);
        Assert.Contains("<Checkbox TValue=\"bool\" @bind-Value=\"EditRead\"", projects, StringComparison.Ordinal);
        Assert.Contains("<SelectField TValue=\"string\" id=\"memory-type\"", memory, StringComparison.Ordinal);
        Assert.Contains("<TextField id=\"memory-content\" Rows=\"12\"", memory, StringComparison.Ordinal);
        Assert.Contains("ValueChanged=\"SetAutoUpdateAsync\"", updates, StringComparison.Ordinal);

        Assert.Matches(@"(?s)\.dashboard-tabs > \.panels > \.tab-panel\s*\{[^}]*overflow-y:\s*auto;", styles);
        Assert.Matches(@"(?s)\.agent-logs-panel\s*\{[^}]*flex:\s*1 1 auto;[^}]*min-height:\s*0;", styles);
        Assert.Matches(@"(?s)\.log-terminal\s*\{[^}]*flex:\s*1 1 auto;[^}]*min-height:\s*0;[^}]*max-height:\s*none;[^}]*margin-top:\s*14px;", styles);
        Assert.Matches(@"(?s)\.audit-panel\s*\{[^}]*flex:\s*1 1 auto;[^}]*min-height:\s*0;", styles);
        Assert.Matches(@"(?s)\.audit-results\s*\{[^}]*flex:\s*1 1 auto;[^}]*overflow-y:\s*auto;", styles);
        Assert.DoesNotContain("input, textarea, select {", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("input[type=\"checkbox\"] {", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("input:focus-visible, textarea:focus-visible, select:focus-visible {", styles, StringComparison.Ordinal);
        Assert.DoesNotContain("max-height: 68vh", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Companion_skills_memory_editor_is_modal_keyboard_scoped_and_refreshes_after_save()
    {
        var root = FindRepositoryRoot();
        var panel = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "SkillsMemoryPanel.razor"));
        var index = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "index.html"));
        var styles = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "css", "app.css"));

        Assert.Contains("<dialog id=\"skills-memory-editor\"", panel, StringComparison.Ordinal);
        Assert.Contains("Id=\"skills-memory-add\" Text=\"Add item\"", panel, StringComparison.Ordinal);
        Assert.Contains("await JS.InvokeVoidAsync(\"mateMcpModal.open\", \"skills-memory-editor\", \"#memory-title\", \"#skills-memory-add\")", panel, StringComparison.Ordinal);
        Assert.Contains("<TextField id=\"memory-title\" autofocus", panel, StringComparison.Ordinal);
        Assert.Contains("await LoadAsync();", panel, StringComparison.Ordinal);
        Assert.Contains("await CloseEditorAsync();", panel, StringComparison.Ordinal);
        Assert.Contains("Title and Content are required.", panel, StringComparison.Ordinal);

        Assert.Contains("window.mateMcpModal", index, StringComparison.Ordinal);
        Assert.Contains("cancelTop()", index, StringComparison.Ordinal);
        Assert.Contains("new Event('cancel', { cancelable: true })", index, StringComparison.Ordinal);
        Assert.Contains("if (dialog.dispatchEvent(cancelEvent)) dialog.close();", index, StringComparison.Ordinal);
        Assert.Contains("open(dialogId, focusSelector, openerSelector)", index, StringComparison.Ordinal);
        Assert.Contains("explicitOpener instanceof HTMLElement ? explicitOpener : document.activeElement", index, StringComparison.Ordinal);
        Assert.Contains("opener.focus({ preventScroll: true })", index, StringComparison.Ordinal);
        Assert.Contains("dialog.showModal()", index, StringComparison.Ordinal);
        Assert.Contains("dialog.addEventListener('close'", index, StringComparison.Ordinal);
        Assert.Contains("const root = openDialogs.length ? openDialogs[openDialogs.length - 1] : document;", index, StringComparison.Ordinal);
        Assert.Contains("root.querySelectorAll(selector)", index, StringComparison.Ordinal);

        Assert.Contains(".companion-dialog {", styles, StringComparison.Ordinal);
        Assert.Contains("class=\"companion-dialog skills-memory-dialog\"", panel, StringComparison.Ordinal);
        Assert.Contains(".memory-filters {", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void Companion_secret_manager_edits_metadata_without_revealing_plaintext_and_requires_explicit_replacement()
    {
        var root = FindRepositoryRoot();
        var main = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "Main.razor"));
        var client = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Services", "AgentApiClient.cs"));
        var index = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "index.html"));

        Assert.Contains("<dialog id=\"secret-editor\"", main, StringComparison.Ordinal);
        Assert.Contains("Id=\"secret-add\" Text=\"Add secret\"", main, StringComparison.Ordinal);
        Assert.Contains("Text=\"Edit\"", main, StringComparison.Ordinal);
        Assert.Contains("Replace stored value", main, StringComparison.Ordinal);
        Assert.Contains("SecretReplaceValue", main, StringComparison.Ordinal);
        Assert.Contains("UpdateSecretMetadataAsync", main, StringComparison.Ordinal);
        Assert.Contains("SecretValue = string.Empty;", main, StringComparison.Ordinal);
        Assert.Contains("stored value was preserved", main, StringComparison.Ordinal);
        Assert.Contains("Enter a new secret value to replace the stored value.", main, StringComparison.Ordinal);
        Assert.Contains("Secrets = await Api.GetSecretsAsync", main, StringComparison.Ordinal);
        Assert.Contains("await CloseSecretEditorAsync();", main, StringComparison.Ordinal);

        Assert.Contains("PutAsJsonAsync($\"secrets/{Uri.EscapeDataString(name)}\"", client, StringComparison.Ordinal);
        Assert.DoesNotContain("GetSecretValue", client, StringComparison.Ordinal);
        Assert.DoesNotContain("ResolveSecret", client, StringComparison.Ordinal);
        Assert.Contains("await JS.InvokeVoidAsync(\"mateMcpModal.open\", \"secret-editor\", \"#secret-name\", \"#secret-add\")", main, StringComparison.Ordinal);
        Assert.Contains("window.mateMcpModal", index, StringComparison.Ordinal);
        Assert.Contains("opener.focus({ preventScroll: true })", index, StringComparison.Ordinal);
        Assert.Contains("const root = openDialogs.length ? openDialogs[openDialogs.length - 1] : document;", index, StringComparison.Ordinal);
    }

    [Fact]
    public void Projects_uses_native_folder_picker_and_reloads_after_mutations_without_local_refresh_button()
    {
        var root = FindRepositoryRoot();
        var projects = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "ProjectsPanel.razor"));
        var program = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "MauiProgram.cs"));
        var projectFile = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "MateMCP.Agent.Companion.csproj"));
        var styles = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "css", "app.css"));

        Assert.DoesNotContain("<Button Text=\"Refresh\" OnClick=\"LoadAsync\" />", projects, StringComparison.Ordinal);
        Assert.Contains("@inject IFolderPicker FolderPicker", projects, StringComparison.Ordinal);
        Assert.Contains("<Button Text=\"Browse...\" OnClick=\"PickWorkspaceAsync\" />", projects, StringComparison.Ordinal);
        Assert.Contains("EditRoot = result.Folder.Path;", projects, StringComparison.Ordinal);
        Assert.Contains("await LoadAsync();", projects, StringComparison.Ordinal);
        Assert.Contains(".UseMauiCommunityToolkit()", program, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<IFolderPicker>(FolderPicker.Default)", program, StringComparison.Ordinal);
        Assert.Contains("CommunityToolkit.Maui", projectFile, StringComparison.Ordinal);
        Assert.Contains(".workspace-path-row", styles, StringComparison.Ordinal);
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