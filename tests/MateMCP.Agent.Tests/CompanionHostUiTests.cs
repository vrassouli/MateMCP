namespace MateMCP.Agent.Tests;

public sealed class CompanionHostUiTests
{
    [Fact]
    public void Companion_treats_missing_shell_session_as_stale_reference()
    {
        var root = FindRepositoryRoot();
        var api = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Services", "AgentApiClient.cs"));

        Assert.Contains("response.StatusCode == HttpStatusCode.NotFound", api, StringComparison.Ordinal);
        Assert.Contains("return null;", api, StringComparison.Ordinal);
        Assert.Contains("response.EnsureSuccessStatusCode();", api, StringComparison.Ordinal);
    }

    [Fact]
    public void Blazor_fatal_error_ui_is_styled_and_reload_does_not_use_anchor_navigation()
    {
        var root = FindRepositoryRoot();
        var index = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "index.html"));
        var css = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "css", "app.css"));

        Assert.Contains("id=\"blazor-error-ui\"", index, StringComparison.Ordinal);
        Assert.Contains("type=\"button\" class=\"reload\"", index, StringComparison.Ordinal);
        Assert.Contains("window.location.reload()", index, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\".\" class=\"reload\"", index, StringComparison.Ordinal);
        Assert.Contains("#blazor-error-ui {", css, StringComparison.Ordinal);
        Assert.Contains("display: none;", css, StringComparison.Ordinal);
        Assert.Contains("#blazor-error-ui .blazor-error-content", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Companion_shell_tracks_system_theme_uses_consistent_title_and_independent_scrolling()
    {
        var root = FindRepositoryRoot();
        var companionRoot = Path.Combine(root, "src", "MateMCP.Agent.Companion");
        var index = File.ReadAllText(Path.Combine(companionRoot, "wwwroot", "index.html"));
        var css = File.ReadAllText(Path.Combine(companionRoot, "wwwroot", "css", "app.css"));
        var main = File.ReadAllText(Path.Combine(companionRoot, "Components", "Main.razor"));
        var imports = File.ReadAllText(Path.Combine(companionRoot, "Components", "_Imports.razor"));
        var app = File.ReadAllText(Path.Combine(companionRoot, "App.xaml.cs"));
        var project = File.ReadAllText(Path.Combine(companionRoot, "MateMCP.Agent.Companion.csproj"));

        Assert.Contains("data-bui-theme=\"light\"", index, StringComparison.Ordinal);
        Assert.Contains("prefers-color-scheme: dark", index, StringComparison.Ordinal);
        Assert.Contains("media.addEventListener('change', applySystemTheme)", index, StringComparison.Ordinal);
        Assert.Contains("<title>MateMCP Companion</title>", index, StringComparison.Ordinal);
        Assert.Contains("<ApplicationTitle>MateMCP Companion</ApplicationTitle>", project, StringComparison.Ordinal);
        Assert.Contains("Title = \"MateMCP Companion\"", app, StringComparison.Ordinal);

        Assert.Contains("MateMCP Companion", main, StringComparison.Ordinal);
        Assert.Contains("Local Agent management</div>", main, StringComparison.Ordinal);
        Assert.DoesNotContain("Local Agent management · Bluent UI", main, StringComparison.Ordinal);
        Assert.Contains("@using Bluent.UI.Icons", imports, StringComparison.Ordinal);
        Assert.Contains("FluentIcons.Home", main, StringComparison.Ordinal);
        Assert.Contains("FluentIcons.ArrowSyncCircle", main, StringComparison.Ordinal);

        Assert.Contains("#app {", css, StringComparison.Ordinal);
        Assert.Contains("height: 100vh;", css, StringComparison.Ordinal);
        Assert.Contains(".sidebar {", css, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto;", css, StringComparison.Ordinal);
        Assert.Contains(".content {", css, StringComparison.Ordinal);
        Assert.Contains("overflow: auto;", css, StringComparison.Ordinal);
        Assert.Contains("var(--colorNeutralBackground1", css, StringComparison.Ordinal);
        Assert.Contains("var(--colorStatusSuccessForeground1", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_approval_notifications_have_an_unpackaged_native_toast_fallback()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "MateMCP.Agent.Companion.csproj"));
        var notifier = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Services", "NativeApprovalNotifier.cs"));

        Assert.Contains("Microsoft.Toolkit.Uwp.Notifications", project, StringComparison.Ordinal);
        Assert.Contains("AppNotificationManager.IsSupported()", notifier, StringComparison.Ordinal);
        Assert.Contains("ToastNotificationManagerCompat.OnActivated", notifier, StringComparison.Ordinal);
        Assert.Contains("new ToastContentBuilder()", notifier, StringComparison.Ordinal);
        Assert.Contains("Approve for session", notifier, StringComparison.Ordinal);
        Assert.Contains("Always allow", notifier, StringComparison.Ordinal);
        Assert.Contains("ToastArguments.Parse", notifier, StringComparison.Ordinal);
        Assert.Contains("DecideFromNotificationAsync", notifier, StringComparison.Ordinal);
    }

    [Fact]
    public void Desktop_updater_downloads_before_exit_and_surfaces_progress_for_both_platforms()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Services", "DesktopUpdateService.cs"));
        var panel = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "DesktopUpdatePanel.razor"));
        var css = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "wwwroot", "css", "app.css"));

        Assert.Contains("BeginUpdateAsync", service, StringComparison.Ordinal);
        Assert.Contains("HttpCompletionOption.ResponseHeadersRead", service, StringComparison.Ordinal);
        Assert.Contains("DownloadAssetAsync", service, StringComparison.Ordinal);
        Assert.Contains("BuildMacInstallScript", service, StringComparison.Ordinal);
        Assert.Contains("BuildWindowsInstallScript", service, StringComparison.Ordinal);
        Assert.Contains("install-desktop-macos.sh\" --no-start", service, StringComparison.Ordinal);
        Assert.Contains("-File $Installer -NoStart", service, StringComparison.Ordinal);
        Assert.Contains("MateMCP-Update", service, StringComparison.Ordinal);
        Assert.Contains("Environment.Exit(0)", service, StringComparison.Ordinal);
        Assert.DoesNotContain("WindowsBootstrap", service, StringComparison.Ordinal);
        Assert.DoesNotContain("MacBootstrap", service, StringComparison.Ordinal);
        Assert.Contains("<progress class=\"update-progress-bar\"", panel, StringComparison.Ordinal);
        Assert.Contains("manual package downloads", panel, StringComparison.Ordinal);
        Assert.Contains("Update failed before installation started", panel, StringComparison.Ordinal);
        Assert.Contains("update-progress-bar", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Automatic_desktop_updates_are_owned_by_the_headless_agent_and_verify_release_digest()
    {
        var root = FindRepositoryRoot();
        var background = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Desktop", "BackgroundDesktopUpdateService.cs"));
        var program = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Program.cs"));
        var api = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Services", "AgentApiClient.cs"));
        var panel = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent.Companion", "Components", "DesktopUpdatePanel.razor"));

        Assert.Contains(": BackgroundService", background, StringComparison.Ordinal);
        Assert.Contains("agent-latest", background, StringComparison.Ordinal);
        Assert.Contains("JsonPropertyName(\"digest\")", background, StringComparison.Ordinal);
        Assert.Contains("CryptographicOperations.FixedTimeEquals", background, StringComparison.Ordinal);
        Assert.Contains("Automatic installation was aborted", background, StringComparison.Ordinal);
        Assert.Contains("activity.TryBeginDrain()", background, StringComparison.Ordinal);
        Assert.Contains("sessions.ActiveSessionCount", background, StringComparison.Ordinal);
        Assert.Contains("approvals.GetPending().Count", background, StringComparison.Ordinal);
        Assert.Contains("install-desktop-windows.ps1", background, StringComparison.Ordinal);
        Assert.Contains("install-desktop-macos.sh", background, StringComparison.Ordinal);
        Assert.Contains("AddHostedService<BackgroundDesktopUpdateService>", program, StringComparison.Ordinal);
        Assert.Contains("/desktop-update/auto", program, StringComparison.Ordinal);
        Assert.Contains("SetDesktopAutoUpdateAsync", api, StringComparison.Ordinal);
        Assert.Contains("even while Companion is closed", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("Updates.AutoUpdateEnabled", panel, StringComparison.Ordinal);
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
