namespace MateMCP.Relay.Tests;

public sealed class ForbidResponseRegressionTests
{
    [Fact]
    public void RelayProgram_UsesPlain403_ForScopeDenial()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "MateMCP.Relay", "Program.cs"));

        Assert.Contains("return Results.StatusCode(StatusCodes.Status403Forbidden);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("return Results.Forbid();", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MateMCP.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate MateMCP repository root.");
    }
}
