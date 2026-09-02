namespace MateMCP.Agent.Tests;

public sealed class CompanionHostUiTests
{
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
        Assert.Contains("#blazor-error-ui { display: none;", css, StringComparison.Ordinal);
        Assert.Contains("#blazor-error-ui .blazor-error-content", css, StringComparison.Ordinal);
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
        Assert.Contains("Companion will stay open while the package downloads", panel, StringComparison.Ordinal);
        Assert.Contains("Update failed before installation started", panel, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(UpdateStatus.LastFailure)", panel, StringComparison.Ordinal);
        Assert.Contains("update-progress-bar", css, StringComparison.Ordinal);
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
