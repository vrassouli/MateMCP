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
