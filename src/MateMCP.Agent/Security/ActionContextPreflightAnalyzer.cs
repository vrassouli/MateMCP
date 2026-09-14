using System.ComponentModel;
using System.Diagnostics;

namespace MateMCP.Agent.Security;

/// <summary>
/// Enriches deterministic action assessments with bounded, read-only local context.
/// Preflight is deliberately scoped to a caller-supplied allowed working directory and
/// never invokes the requested shell command.
/// </summary>
public sealed class ActionContextPreflightAnalyzer
{
    private const int MaxPreviewChars = 4_000;
    private const int MaxDirectoryEntries = 200;

    public static ActionContextPreflightAnalyzer Default { get; } = new();

    public async Task<ActionImpactAssessment> EnrichAsync(
        ActionAssessmentContext context,
        ActionImpactAssessment assessment,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetWorkingDirectory(context, out var allowedRoot)) return assessment;

        string root;
        try { root = Path.GetFullPath(allowedRoot); }
        catch { return assessment; }
        if (!Directory.Exists(root)) return assessment;

        var reasons = assessment.Reasons.ToList();
        var resources = assessment.AffectedResources.ToList();
        var preview = new List<string>();
        var risk = assessment.Risk;
        var confidence = assessment.Confidence;
        var production = assessment.ProductionLikelihood;
        var saferAlternative = assessment.SaferAlternative;

        foreach (var candidate in ExtractFilesystemCandidates(context, assessment, root).Take(6))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveInsideRoot(root, candidate, out var fullPath))
            {
                preview.Add($"Skipped out-of-scope target: {Bound(candidate, 160)}");
                reasons.Add("A filesystem-like target resolved outside the authorized working directory, so MateMCP did not inspect it.");
                continue;
            }

            if (!resources.Contains(fullPath, PathComparer)) resources.Add(fullPath);
            InspectPath(fullPath, preview);

            if (IsSensitiveSystemPath(fullPath))
            {
                risk = MaxRisk(risk, ActionRiskLevel.High);
                confidence = MaxConfidence(confidence, AssessmentConfidence.High);
                reasons.Add($"The target is inside a sensitive operating-system path: {Bound(fullPath, 180)}.");
            }

            if (LooksProductionPath(fullPath))
            {
                production = true;
                risk = MaxRisk(risk, ActionRiskLevel.High);
                reasons.Add("The inspected path contains an explicit production/prod/live environment marker.");
            }
        }

        if (IsGitRelevant(context, assessment))
        {
            var git = await InspectGitAsync(root, cancellationToken);
            if (git is not null)
            {
                preview.Add($"Git repository: {git.RepositoryRoot}");
                if (!string.IsNullOrWhiteSpace(git.Branch)) preview.Add($"Git branch: {git.Branch}");
                preview.Add(git.DirtyCount == 0
                    ? "Git working tree: clean"
                    : $"Git working tree: {git.DirtyCount} changed/untracked item(s) (bounded status scan)");

                if (git.DirtyCount > 0 && assessment.Destructive)
                {
                    risk = MaxRisk(risk, ActionRiskLevel.High);
                    reasons.Add("The Git working tree has local changes that a destructive Git action could discard or overwrite.");
                    saferAlternative ??= "Review `git status`/`git diff` and create a stash or backup branch before the destructive Git operation.";
                }

                if (git.Branch is not null && LooksProductionBranch(git.Branch))
                {
                    production = true;
                    risk = MaxRisk(risk, ActionRiskLevel.High);
                    reasons.Add($"The current Git branch '{Bound(git.Branch, 80)}' has an explicit production/release marker.");
                }
            }
        }

        var dryRun = BuildDryRunHint(context, assessment);
        if (dryRun is not null)
        {
            preview.Add(dryRun);
            saferAlternative ??= dryRun.Replace("Supported preflight: ", string.Empty, StringComparison.Ordinal);
        }

        if (preview.Count == 0 && reasons.SequenceEqual(assessment.Reasons)) return assessment;

        var mergedPreview = string.Join(Environment.NewLine, preview.Distinct(StringComparer.Ordinal));
        if (!string.IsNullOrWhiteSpace(assessment.Preview))
            mergedPreview = assessment.Preview + Environment.NewLine + mergedPreview;

        return assessment with
        {
            Risk = risk,
            Confidence = confidence,
            AffectedResources = resources.Distinct(PathComparer).Take(12).ToArray(),
            ProductionLikelihood = production,
            Reasons = reasons.Distinct(StringComparer.Ordinal).Take(12).ToArray(),
            SaferAlternative = saferAlternative,
            Preview = Bound(mergedPreview, MaxPreviewChars)
        };
    }

    private static bool TryGetWorkingDirectory(ActionAssessmentContext context, out string workingDirectory)
    {
        workingDirectory = string.Empty;
        if (context.Arguments is null) return false;
        foreach (var key in new[] { "workingDirectory", "working_directory", "cwd" })
        {
            if (context.Arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                workingDirectory = value;
                return true;
            }
        }
        return false;
    }

    private static IEnumerable<string> ExtractFilesystemCandidates(
        ActionAssessmentContext context,
        ActionImpactAssessment assessment,
        string root)
    {
        if (context.Arguments is not null)
        {
            foreach (var key in new[] { "path", "file", "targetPath", "source", "destination" })
                if (context.Arguments.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                    yield return value;
        }

        if (!string.Equals(context.ActionType, "shell", StringComparison.OrdinalIgnoreCase)
            && !context.Capability.StartsWith("shell.", StringComparison.OrdinalIgnoreCase))
            yield break;

        if (assessment.IntentCategory != "delete") yield break;

        var tokens = ShellActionImpactAnalyzer.Tokenize(context.Summary);
        if (tokens.Count == 0) yield break;

        var commandIndex = 0;
        if (Normalize(tokens[0]) == "sudo")
        {
            commandIndex = 1;
            while (commandIndex < tokens.Count && tokens[commandIndex].StartsWith("-", StringComparison.Ordinal)) commandIndex++;
        }
        if (commandIndex >= tokens.Count) yield break;

        for (var i = commandIndex + 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (string.IsNullOrWhiteSpace(token) || IsOperator(token) || IsOption(token)) continue;
            if (token is "--") continue;
            if (token.Contains('*') || token.Contains('?'))
            {
                foreach (var match in ExpandBoundedWildcard(root, token)) yield return match;
                continue;
            }
            yield return token;
        }
    }

    private static IEnumerable<string> ExpandBoundedWildcard(string root, string token)
    {
        string fullPattern;
        try { fullPattern = Path.GetFullPath(Path.IsPathRooted(token) ? token : Path.Combine(root, token)); }
        catch { yield break; }

        var directory = Path.GetDirectoryName(fullPattern);
        var pattern = Path.GetFileName(fullPattern);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(pattern)) yield break;
        if (!TryResolveInsideRoot(root, directory, out var safeDirectory) || !Directory.Exists(safeDirectory)) yield break;

        IEnumerable<string> matches;
        try { matches = Directory.EnumerateFileSystemEntries(safeDirectory, pattern, SearchOption.TopDirectoryOnly); }
        catch { yield break; }
        foreach (var match in matches.Take(25)) yield return match;
    }

    private static void InspectPath(string fullPath, List<string> preview)
    {
        try
        {
            if (File.Exists(fullPath))
            {
                var info = new FileInfo(fullPath);
                preview.Add($"Target file exists: {fullPath} ({info.Length} bytes)");
                return;
            }
            if (Directory.Exists(fullPath))
            {
                var count = Directory.EnumerateFileSystemEntries(fullPath, "*", SearchOption.TopDirectoryOnly)
                    .Take(MaxDirectoryEntries + 1).Count();
                preview.Add(count > MaxDirectoryEntries
                    ? $"Target directory exists: {fullPath} (> {MaxDirectoryEntries} direct entries)"
                    : $"Target directory exists: {fullPath} ({count} direct entr{(count == 1 ? "y" : "ies")})");
                return;
            }
            preview.Add($"Target does not currently exist: {fullPath}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            preview.Add($"Target exists or resolves in scope, but metadata could not be fully inspected: {fullPath}");
        }
    }

    private static async Task<GitPreflight?> InspectGitAsync(string allowedRoot, CancellationToken cancellationToken)
    {
        var repoRoot = await RunGitAsync(allowedRoot, ["rev-parse", "--show-toplevel"], cancellationToken);
        if (string.IsNullOrWhiteSpace(repoRoot)) return null;
        repoRoot = repoRoot.Trim();
        if (!TryResolveInsideRoot(allowedRoot, repoRoot, out var safeRepoRoot)) return null;

        var status = await RunGitAsync(safeRepoRoot, ["status", "--porcelain=v1", "-uno"], cancellationToken) ?? string.Empty;
        var untracked = await RunGitAsync(safeRepoRoot, ["ls-files", "--others", "--exclude-standard"], cancellationToken) ?? string.Empty;
        var branch = await RunGitAsync(safeRepoRoot, ["branch", "--show-current"], cancellationToken);
        var dirty = CountNonEmptyLines(status, 200) + CountNonEmptyLines(untracked, 200);
        return new GitPreflight(safeRepoRoot, string.IsNullOrWhiteSpace(branch) ? null : branch.Trim(), dirty);
    }

    private static async Task<string?> RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
            using var process = Process.Start(psi);
            if (process is null) return null;
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var result = await stdout;
            return process.ExitCode == 0 ? result : null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    private static string? BuildDryRunHint(ActionAssessmentContext context, ActionImpactAssessment assessment)
    {
        if (assessment.IntentCategory != "package-mutation") return null;
        var tokens = ShellActionImpactAnalyzer.Tokenize(context.Summary);
        if (tokens.Count == 0) return null;
        var first = Normalize(tokens[0]);
        if (first == "sudo" && tokens.Count > 1) first = Normalize(tokens.Skip(1).FirstOrDefault(x => !x.StartsWith("-", StringComparison.Ordinal)) ?? string.Empty);
        return first switch
        {
            "apt" or "apt-get" => "Supported preflight: review the transaction with `apt-get -s ...` before applying package changes.",
            "npm" or "pnpm" or "yarn" => "Supported preflight: use the package manager's dry-run/lockfile review mode before applying dependency changes when available.",
            "brew" => "Supported preflight: inspect package information and dependency changes before applying the Homebrew mutation.",
            "winget" or "choco" or "scoop" => "Supported preflight: inspect the selected package/version and current installation state before applying the package mutation.",
            _ => null
        };
    }

    private static bool IsGitRelevant(ActionAssessmentContext context, ActionImpactAssessment assessment)
        => assessment.IntentCategory == "git-mutation"
            || context.Summary.TrimStart().StartsWith("git ", StringComparison.OrdinalIgnoreCase)
            || context.Summary.TrimStart().StartsWith("sudo git ", StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveInsideRoot(string root, string candidate, out string fullPath)
    {
        fullPath = string.Empty;
        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            fullPath = Path.GetFullPath(Path.IsPathRooted(candidate) ? candidate : Path.Combine(normalizedRoot, candidate));
            if (string.Equals(fullPath, normalizedRoot, PathComparison)) return true;
            var prefix = normalizedRoot + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, PathComparison);
        }
        catch { return false; }
    }

    private static bool IsSensitiveSystemPath(string path)
    {
        var full = path.Replace('\\', '/');
        if (OperatingSystem.IsWindows())
            return StartsWithAny(full, "C:/Windows/", "C:/Program Files/", "C:/Program Files (x86)/", "C:/ProgramData/");
        return StartsWithAny(full, "/etc/", "/usr/", "/bin/", "/sbin/", "/System/", "/Library/", "/Applications/");
    }

    private static bool LooksProductionPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(x => x.Equals("prod", StringComparison.OrdinalIgnoreCase)
            || x.Equals("production", StringComparison.OrdinalIgnoreCase)
            || x.Equals("live", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksProductionBranch(string branch)
        => branch.Equals("prod", StringComparison.OrdinalIgnoreCase)
            || branch.Equals("production", StringComparison.OrdinalIgnoreCase)
            || branch.Equals("live", StringComparison.OrdinalIgnoreCase)
            || branch.StartsWith("release/", StringComparison.OrdinalIgnoreCase)
            || branch.StartsWith("production/", StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithAny(string value, params string[] prefixes)
        => prefixes.Any(x => value.StartsWith(x, PathComparison));
    private static bool IsOperator(string token) => token is ";" or "|" or "||" or "&&" or ">" or ">>" or "<" or "<<";
    private static bool IsOption(string token) => token.StartsWith("-", StringComparison.Ordinal) || token.StartsWith("/", StringComparison.Ordinal) && !Path.IsPathRooted(token);
    private static string Normalize(string value) => Path.GetFileName(value.Replace('\\', '/')).Replace(".exe", string.Empty, StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
    private static int CountNonEmptyLines(string value, int cap) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(cap).Count();
    private static string Bound(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private static ActionRiskLevel MaxRisk(ActionRiskLevel left, ActionRiskLevel right)
    {
        static int Weight(ActionRiskLevel value) => value switch
        {
            ActionRiskLevel.Low => 0,
            ActionRiskLevel.Medium => 1,
            ActionRiskLevel.Unknown => 2,
            ActionRiskLevel.High => 3,
            ActionRiskLevel.Critical => 4,
            _ => 2
        };
        return Weight(right) > Weight(left) ? right : left;
    }

    private static AssessmentConfidence MaxConfidence(AssessmentConfidence left, AssessmentConfidence right)
        => (AssessmentConfidence)Math.Max((int)left, (int)right);

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record GitPreflight(string RepositoryRoot, string? Branch, int DirtyCount);
}
