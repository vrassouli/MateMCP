using System.Diagnostics;
using MateMCP.Agent.Security;

namespace MateMCP.Agent.Tests;

public sealed class ActionContextPreflightAnalyzerTests
{
    private readonly ActionImpactAnalyzer _impact = new();
    private readonly ActionContextPreflightAnalyzer _preflight = new();

    [Fact]
    public async Task DeletePreview_ReportsBoundedFilesystemFacts_WithoutMutatingTarget()
    {
        using var temp = new TempDirectory();
        var target = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(target);
        var first = Path.Combine(target, "a.txt");
        var second = Path.Combine(target, "b.txt");
        await File.WriteAllTextAsync(first, "a");
        await File.WriteAllTextAsync(second, "bb");

        var context = Shell(temp.Path, "rm -rf target");
        var result = await EnrichAsync(context);

        Assert.Contains("Target directory exists", result.Preview, StringComparison.Ordinal);
        Assert.Contains("2 direct entries", result.Preview, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(target), result.AffectedResources, PathComparer);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task OutOfScopeTarget_IsNotInspectedOrAddedAsAffectedResource()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(temp.Path, "outside.txt");
        await File.WriteAllTextAsync(outside, "do not inspect");

        var context = Shell(root, "rm ../outside.txt");
        var result = await EnrichAsync(context);

        Assert.Contains("Skipped out-of-scope target", result.Preview, StringComparison.Ordinal);
        Assert.DoesNotContain(Path.GetFullPath(outside), result.AffectedResources, PathComparer);
        Assert.True(File.Exists(outside));
        Assert.Contains(result.Reasons, x => x.Contains("outside the authorized working directory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExplicitProductionPath_RaisesProductionLikelihood()
    {
        using var temp = new TempDirectory();
        var production = Path.Combine(temp.Path, "production");
        Directory.CreateDirectory(production);
        var config = Path.Combine(production, "config.json");
        await File.WriteAllTextAsync(config, "{}");

        var result = await EnrichAsync(Shell(temp.Path, "rm production/config.json"));

        Assert.True(result.ProductionLikelihood);
        Assert.Equal(ActionRiskLevel.High, result.Risk);
        Assert.Contains(result.Reasons, x => x.Contains("production/prod/live", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(config));
    }

    [Fact]
    public async Task DestructiveGit_InDirtyRepository_ReportsLocalChangesAndSaferAlternative()
    {
        if (!GitAvailable()) return;

        using var temp = new TempDirectory();
        RunGit(temp.Path, "init");
        RunGit(temp.Path, "config", "user.email", "tests@matemcp.local");
        RunGit(temp.Path, "config", "user.name", "MateMCP Tests");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "tracked.txt"), "initial");
        RunGit(temp.Path, "add", "tracked.txt");
        RunGit(temp.Path, "commit", "-m", "initial");
        await File.AppendAllTextAsync(Path.Combine(temp.Path, "tracked.txt"), "\nchanged");
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "untracked.txt"), "new");

        var result = await EnrichAsync(Shell(temp.Path, "git reset --hard HEAD"));

        Assert.Contains("Git working tree:", result.Preview, StringComparison.Ordinal);
        Assert.DoesNotContain("Git working tree: clean", result.Preview, StringComparison.Ordinal);
        Assert.Contains(result.Reasons, x => x.Contains("local changes", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("stash", result.SaferAlternative, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("initial\nchanged", (await File.ReadAllTextAsync(Path.Combine(temp.Path, "tracked.txt"))).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(temp.Path, "untracked.txt")));
    }

    [Fact]
    public async Task PackageMutation_AddsSupportedDryRunGuidance()
    {
        using var temp = new TempDirectory();
        var result = await EnrichAsync(Shell(temp.Path, "sudo apt install nginx"));

        Assert.Equal(ActionRiskLevel.High, result.Risk);
        Assert.Contains("apt-get -s", result.Preview, StringComparison.Ordinal);
        Assert.Contains("apt-get -s", result.SaferAlternative, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContextWithoutAuthorizedWorkingDirectory_DoesNotTouchLocalState()
    {
        var context = new ActionAssessmentContext("shell.exec", "project:Demo", "rm -rf target", "shell");
        var baseline = _impact.Assess(context);

        var result = await _preflight.EnrichAsync(context, baseline);

        Assert.Same(baseline, result);
        Assert.Null(result.Preview);
    }

    private async Task<ActionImpactAssessment> EnrichAsync(ActionAssessmentContext context)
        => await _preflight.EnrichAsync(context, _impact.Assess(context));

    private static ActionAssessmentContext Shell(string workingDirectory, string command)
        => new(
            "shell.exec",
            "project:Test",
            command,
            "shell",
            new Dictionary<string, string?> { ["workingDirectory"] = workingDirectory });

    private static bool GitAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "--version" }
            });
            process?.WaitForExit(2_000);
            return process?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("git could not start");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stdout}\n{stderr}");
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "matemcp-preflight-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { }
        }
    }
}
