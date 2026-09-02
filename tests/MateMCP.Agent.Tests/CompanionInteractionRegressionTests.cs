namespace MateMCP.Agent.Tests;

public sealed class CompanionInteractionRegressionTests
{
    [Fact]
    public void Companion_shell_navigation_shows_only_active_session_count()
    {
        var main = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "MateMCP.Agent.Companion", "Components", "Main.razor"));

        Assert.Contains("private int ActiveShellCount => ShellSessions.Count(x => !x.Exited);", main, StringComparison.Ordinal);
        Assert.Contains("Shell@(ActiveShellCount > 0 ? $\" ({ActiveShellCount})\" : string.Empty)", main, StringComparison.Ordinal);
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
