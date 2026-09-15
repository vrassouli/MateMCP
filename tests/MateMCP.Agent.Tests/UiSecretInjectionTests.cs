using MateMCP.Agent.Security;
using MateMCP.Agent.Tools;

namespace MateMCP.Agent.Tests;

public sealed class UiSecretInjectionTests
{
    [Fact]
    public void Ui_secret_tool_has_a_distinct_secret_policy_name()
    {
        Assert.Equal("ui_fill_secret", UserSecretInfo.UiFillSecretTool);
        var info = new UserSecretInfo("demo", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            AllowedTools: [UserSecretInfo.UiFillSecretTool]);
        Assert.True(info.IsAllowedForTool("ui_fill_secret"));
        Assert.False(info.IsAllowedForTool("shell_session_send_secret"));
    }

    [Fact]
    public void Ui_secret_pipeline_binds_exact_element_and_redacts_returned_value()
    {
        var root = FindRepositoryRoot();
        var tools = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Tools", "DesktopSemanticTools.cs"));
        var semantic = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Desktop", "SemanticUiService.cs"));
        var windows = File.ReadAllText(Path.Combine(root, "src", "MateMCP.WindowsDesktopHelper", "Program.cs"));
        var mac = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Desktop", "MacAccessibility.cs"));

        Assert.Contains("_semantic.ResolveAsync(windowId, selector", tools, StringComparison.Ordinal);
        Assert.Contains("_semantic.FillSecretAsync(windowId, selected, value", tools, StringComparison.Ordinal);
        Assert.DoesNotContain("value = value", tools, StringComparison.Ordinal);
        Assert.Contains("FillSecretAsync(string windowId, UiElementInfo expected, string secret", semantic, StringComparison.Ordinal);
        Assert.Contains("entries.SingleOrDefault(x => string.Equals(x.Info.Id, request.ElementId", windows, StringComparison.Ordinal);
        Assert.Contains("SameBinding(selected.Info, request.Expected)", windows, StringComparison.Ordinal);
        Assert.Contains("item.Value = null", windows, StringComparison.Ordinal);
        Assert.Contains("SameBinding(selected, expected)", mac, StringComparison.Ordinal);
        Assert.Contains("return updated with { Value = null }", mac, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_ui_type_still_refuses_protected_fields()
    {
        var root = FindRepositoryRoot();
        var windows = File.ReadAllText(Path.Combine(root, "src", "MateMCP.WindowsDesktopHelper", "Program.cs"));
        var macActions = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Desktop", "MacSemanticUiActionService.cs"));

        Assert.Contains("if (selected.Info.Protected) throw new ProtectedException", windows, StringComparison.Ordinal);
        Assert.Contains("action == \"value\" && selected.Protected", macActions, StringComparison.Ordinal);
    }


    [Fact]
    public void Visible_unprotected_secret_is_redacted_and_blocks_capture_until_cleared()
    {
        var guard = new MateMCP.Agent.Desktop.SensitiveUiGuard();
        guard.Mark("window-1", "field-1");

        var secretSnapshot = Snapshot("window-1", truncated: false, value: "this-must-never-leave-the-agent");
        var redacted = guard.RedactAndReconcile(secretSnapshot);

        Assert.True(guard.Active);
        Assert.Null(redacted.Elements.Single().Value);

        var cleared = guard.RedactAndReconcile(Snapshot("window-1", truncated: false, value: ""));
        Assert.False(guard.Active);
        Assert.Equal("", cleared.Elements.Single().Value);
    }

    [Fact]
    public void Truncated_snapshot_cannot_accidentally_release_a_secret_capture_guard()
    {
        var guard = new MateMCP.Agent.Desktop.SensitiveUiGuard();
        guard.Mark("window-1", "field-1");

        var truncated = new MateMCP.Agent.Desktop.UiSnapshot("window-1", "test", true, []);
        guard.RedactAndReconcile(truncated);
        Assert.True(guard.Active);

        var complete = new MateMCP.Agent.Desktop.UiSnapshot("window-1", "test", false, []);
        guard.RedactAndReconcile(complete);
        Assert.False(guard.Active);
    }

    [Fact]
    public void Pixel_and_browser_capture_paths_consult_the_visible_secret_guard()
    {
        var root = FindRepositoryRoot();
        var desktop = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Tools", "DesktopVisionTools.cs"));
        var browser = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Tools", "BrowserTools.cs"));
        var visual = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Tools", "BrowserVisualQaTools.cs"));
        var preview = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Desktop", "ComputerUsePreviewService.cs"));

        Assert.Contains("SensitiveUiGuard.Shared.Active", desktop, StringComparison.Ordinal);
        Assert.Contains("SensitiveUiGuard.Shared.Active", browser, StringComparison.Ordinal);
        Assert.Contains("SensitiveUiGuard.Shared.Active", visual, StringComparison.Ordinal);
        Assert.Contains("SensitiveUiGuard.Shared.Active", preview, StringComparison.Ordinal);
    }

    private static MateMCP.Agent.Desktop.UiSnapshot Snapshot(string windowId, bool truncated, string? value)
        => new(windowId, "test", truncated,
        [
            new MateMCP.Agent.Desktop.UiElementInfo(
                "field-1", null, "textbox", "API token", "token-input", value,
                Protected: false, Enabled: true, Focused: false,
                Selected: null, Checked: null, Expanded: null, Bounds: null, Actions: ["set-value"])
        ]);

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
