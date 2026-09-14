using System.Text;

namespace MateMCP.Agent.Security;

public sealed class ActionImpactAnalyzer
{
    private readonly IReadOnlyList<IActionImpactAnalyzer> _analyzers;

    public ActionImpactAnalyzer(IEnumerable<IActionImpactAnalyzer>? analyzers = null)
    {
        _analyzers = (analyzers ?? DefaultAnalyzers()).ToArray();
    }

    public ActionImpactAssessment Assess(ActionAssessmentContext context)
    {
        foreach (var analyzer in _analyzers)
        {
            if (analyzer.CanAnalyze(context)) return analyzer.Analyze(context);
        }

        return Unknown(context, "No deterministic analyzer recognized this action.");
    }

    public static ActionImpactAnalyzer Default { get; } = new();

    private static IEnumerable<IActionImpactAnalyzer> DefaultAnalyzers()
    {
        yield return new ShellActionImpactAnalyzer();
        yield return new StructuredActionImpactAnalyzer();
        yield return new UnknownActionImpactAnalyzer();
    }

    internal static ActionImpactAssessment Unknown(ActionAssessmentContext context, string reason)
        => new(
            ActionRiskLevel.Unknown,
            AssessmentConfidence.Low,
            IntentCategory: "unknown",
            Effect: "MateMCP cannot confidently predict the action's effect from the available deterministic signals.",
            AffectedResources: Resource(context),
            Scope: Scope(context),
            Destructive: false,
            Reversible: ActionReversibility.Unknown,
            RequiresElevation: false,
            CredentialExposure: LooksCredentialRelated(context.Summary),
            NetworkEffect: false,
            PersistenceEffect: false,
            ProductionLikelihood: LooksProductionRelated(context.Target + " " + context.Summary),
            Reasons: [reason, "Uncertainty is treated conservatively and is not evidence that an action is safe."]);

    internal static IReadOnlyList<string> Resource(ActionAssessmentContext context)
        => string.IsNullOrWhiteSpace(context.Target) ? [] : [Bound(context.Target, 240)];

    internal static string Scope(ActionAssessmentContext context)
        => string.IsNullOrWhiteSpace(context.Target) ? "unspecified" : Bound(context.Target, 240);

    internal static bool LooksCredentialRelated(string value)
    {
        var lower = value.ToLowerInvariant();
        return lower.Contains("password", StringComparison.Ordinal)
            || lower.Contains("passwd", StringComparison.Ordinal)
            || lower.Contains("secret", StringComparison.Ordinal)
            || lower.Contains("credential", StringComparison.Ordinal)
            || lower.Contains("token", StringComparison.Ordinal)
            || lower.Contains("api key", StringComparison.Ordinal)
            || lower.Contains("apikey", StringComparison.Ordinal);
    }

    internal static bool LooksProductionRelated(string value)
    {
        var lower = value.ToLowerInvariant();
        return lower.Contains("production", StringComparison.Ordinal)
            || lower.Contains("prod", StringComparison.Ordinal)
            || lower.Contains("live", StringComparison.Ordinal)
            || lower.Contains("release", StringComparison.Ordinal);
    }

    internal static string Bound(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}

internal sealed class StructuredActionImpactAnalyzer : IActionImpactAnalyzer
{
    public bool CanAnalyze(ActionAssessmentContext context)
        => !string.IsNullOrWhiteSpace(context.Capability);

    public ActionImpactAssessment Analyze(ActionAssessmentContext context)
    {
        var capability = context.Capability.Trim().ToLowerInvariant();
        var resources = ActionImpactAnalyzer.Resource(context);
        var scope = ActionImpactAnalyzer.Scope(context);
        var production = ActionImpactAnalyzer.LooksProductionRelated(context.Target + " " + context.Summary);

        if (capability.Contains("read", StringComparison.Ordinal)
            || capability.Contains("list", StringComparison.Ordinal)
            || capability.Contains("status", StringComparison.Ordinal)
            || capability.Contains("view", StringComparison.Ordinal)
            || capability.Contains("snapshot", StringComparison.Ordinal))
        {
            return new(ActionRiskLevel.Low, AssessmentConfidence.High, "read", "Reads or inspects state without an expected persistent mutation.", resources, scope,
                false, ActionReversibility.Yes, false, ActionImpactAnalyzer.LooksCredentialRelated(context.Summary), false, false, production,
                ["The MateMCP capability is structured as read/inspect-only."]);
        }

        if (capability.Contains("secret", StringComparison.Ordinal) || capability.Contains("credential", StringComparison.Ordinal))
        {
            return new(ActionRiskLevel.High, AssessmentConfidence.High, "credential-access", "Accesses or injects credential material that may authorize actions outside the current process.", resources, scope,
                false, ActionReversibility.Unknown, false, true, false, false, production,
                ["The structured MateMCP capability handles secrets or credentials."]);
        }

        if (capability.Contains("delete", StringComparison.Ordinal) || capability.Contains("remove", StringComparison.Ordinal) || capability.Contains("revoke", StringComparison.Ordinal))
        {
            return new(ActionRiskLevel.High, AssessmentConfidence.High, "delete", "Deletes, removes, or revokes an identified resource.", resources, scope,
                true, ActionReversibility.Unknown, false, false, false, true, production,
                ["The structured MateMCP capability is explicitly destructive."],
                SaferAlternative: "Review the target and create a backup or use a reversible operation when available.");
        }

        if (capability.Contains("write", StringComparison.Ordinal) || capability.Contains("create", StringComparison.Ordinal) || capability.Contains("update", StringComparison.Ordinal) || capability.Contains("upload", StringComparison.Ordinal))
        {
            return new(ActionRiskLevel.Medium, AssessmentConfidence.High, "write", "Creates or modifies persistent data in the requested scope.", resources, scope,
                false, ActionReversibility.Partial, false, ActionImpactAnalyzer.LooksCredentialRelated(context.Summary), false, true, production,
                ["The structured MateMCP capability mutates persistent data."]);
        }

        if (capability.StartsWith("computer.", StringComparison.Ordinal) || capability.StartsWith("desktop.", StringComparison.Ordinal) || capability.StartsWith("browser.", StringComparison.Ordinal))
        {
            return new(ActionRiskLevel.Medium, AssessmentConfidence.Medium, "computer-use", "May interact with application state; exact external effects depend on the selected UI target.", resources, scope,
                false, ActionReversibility.Unknown, false, ActionImpactAnalyzer.LooksCredentialRelated(context.Summary), true, true, production,
                ["Computer/UI interaction can submit or mutate state even when the raw input is simple."]);
        }

        return ActionImpactAnalyzer.Unknown(context, $"The structured capability '{ActionImpactAnalyzer.Bound(context.Capability, 80)}' has no dedicated deterministic rule yet.");
    }
}

internal sealed class UnknownActionImpactAnalyzer : IActionImpactAnalyzer
{
    public bool CanAnalyze(ActionAssessmentContext context) => true;
    public ActionImpactAssessment Analyze(ActionAssessmentContext context)
        => ActionImpactAnalyzer.Unknown(context, "The action did not match a known structured or shell analyzer.");
}

internal sealed class ShellActionImpactAnalyzer : IActionImpactAnalyzer
{
    private static readonly string[] ReadOnlyCommands =
    [
        "pwd", "ls", "dir", "cat", "type", "head", "tail", "grep", "rg", "find", "which", "where", "whoami",
        "get-childitem", "get-content", "get-item", "get-location", "select-string"
    ];

    public bool CanAnalyze(ActionAssessmentContext context)
        => context.Capability.StartsWith("shell.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(context.ActionType, "shell", StringComparison.OrdinalIgnoreCase);

    public ActionImpactAssessment Analyze(ActionAssessmentContext context)
    {
        var command = context.Summary.Trim();
        if (string.IsNullOrWhiteSpace(command)) return ActionImpactAnalyzer.Unknown(context, "The shell command is empty.");

        var tokens = Tokenize(command);
        if (tokens.Count == 0) return ActionImpactAnalyzer.Unknown(context, "The shell command could not be tokenized.");

        var lower = command.ToLowerInvariant();
        var first = NormalizeExecutable(tokens[0]);
        var resources = ActionImpactAnalyzer.Resource(context);
        var scope = ActionImpactAnalyzer.Scope(context);
        var reasons = new List<string>();
        var destructive = false;
        var elevation = ContainsWord(tokens, "sudo") || ContainsWord(tokens, "runas") || lower.Contains("-verb runas", StringComparison.Ordinal);
        var credential = ActionImpactAnalyzer.LooksCredentialRelated(command);
        var network = LooksNetworkRelated(lower);
        var persistence = false;
        var production = ActionImpactAnalyzer.LooksProductionRelated(context.Target + " " + command);
        var risk = ActionRiskLevel.Unknown;
        var confidence = AssessmentConfidence.Medium;
        var category = "shell-command";
        var effect = "Executes a shell command whose exact behavior depends on the executable and arguments.";
        var reversible = ActionReversibility.Unknown;
        string? saferAlternative = null;

        if (elevation)
        {
            reasons.Add("The command requests or may request elevated privileges.");
            risk = Max(risk, ActionRiskLevel.High);
        }

        if (ContainsShellControlOperator(tokens))
        {
            reasons.Add("The command contains chaining, piping, redirection, or command-substitution syntax, so multiple effects may occur.");
            if (risk == ActionRiskLevel.Unknown) risk = ActionRiskLevel.Medium;
        }

        if (IsCatastrophic(lower, first))
        {
            risk = ActionRiskLevel.Critical;
            confidence = AssessmentConfidence.High;
            category = "system-destructive";
            effect = "May erase storage, broadly destroy system data, or stop/reboot the machine.";
            destructive = true;
            persistence = true;
            reversible = ActionReversibility.No;
            reasons.Add("A known system-destructive command or argument pattern was detected.");
            saferAlternative = "Use a scoped target, dry-run/list operation, or verified backup before executing the destructive command.";
        }
        else if (IsDeleteCommand(first, lower))
        {
            risk = ActionRiskLevel.High;
            confidence = AssessmentConfidence.High;
            category = "delete";
            effect = "Deletes filesystem content; recursive, force, or wildcard forms can affect many files without recovery.";
            destructive = true;
            persistence = true;
            reversible = ActionReversibility.Unknown;
            reasons.Add("A filesystem delete/remove command was detected.");
            if (HasAny(lower, " -r", " -rf", " -fr", "-recurse", " /s", " /q", " -force", "*")) reasons.Add("Recursive, force, quiet, or wildcard behavior broadens the deletion scope.");
            saferAlternative = "List the exact targets first and prefer a reversible trash/backup workflow where possible.";
        }
        else if (IsDestructiveGit(lower))
        {
            risk = lower.Contains("push", StringComparison.Ordinal) && HasAny(lower, "--force", " -f") ? ActionRiskLevel.Critical : ActionRiskLevel.High;
            confidence = AssessmentConfidence.High;
            category = "git-mutation";
            effect = risk == ActionRiskLevel.Critical
                ? "May rewrite remote Git history and affect other collaborators."
                : "May discard or overwrite local Git working-tree/index/history state.";
            destructive = true;
            persistence = true;
            network |= lower.Contains("push", StringComparison.Ordinal);
            reversible = ActionReversibility.Unknown;
            reasons.Add("A known destructive Git operation was detected.");
            saferAlternative = "Inspect `git status`/`git diff` and create a backup branch or stash before destructive history/worktree changes.";
        }
        else if (IsPackageMutation(first, lower))
        {
            risk = ActionRiskLevel.Medium;
            confidence = AssessmentConfidence.High;
            category = "package-mutation";
            effect = "Installs, removes, or changes software packages and may execute package lifecycle scripts.";
            persistence = true;
            network = true;
            reversible = ActionReversibility.Partial;
            reasons.Add("A package-manager mutation was detected.");
        }
        else if (IsSystemMutation(first, lower))
        {
            risk = ActionRiskLevel.High;
            confidence = AssessmentConfidence.High;
            category = "system-configuration";
            effect = "Changes service, firewall, registry, permission, or other system configuration.";
            persistence = true;
            network |= lower.Contains("firewall", StringComparison.Ordinal) || first is "netsh" or "ufw";
            reversible = ActionReversibility.Partial;
            reasons.Add("A known system-configuration mutation was detected.");
        }
        else if (IsReadOnly(first, lower) && !ContainsMutationRedirection(tokens))
        {
            risk = ActionRiskLevel.Low;
            confidence = AssessmentConfidence.High;
            category = "read";
            effect = "Reads or inspects local state without an expected persistent mutation.";
            reversible = ActionReversibility.Yes;
            reasons.Add("The command matches a known read/inspection operation and no output redirection was detected.");
        }
        else if (LooksNetworkRelated(lower))
        {
            risk = ActionRiskLevel.Medium;
            confidence = AssessmentConfidence.Medium;
            category = "network";
            effect = "Communicates with a remote endpoint or may transfer data outside the local machine.";
            network = true;
            reversible = ActionReversibility.Unknown;
            reasons.Add("A network-capable command or URL-like target was detected.");
        }

        if (credential)
        {
            reasons.Add("The command text references credential-like material or credential handling.");
            risk = Max(risk, ActionRiskLevel.High);
        }
        if (production)
        {
            reasons.Add("The target or command contains production/live/release indicators.");
            risk = Max(risk, ActionRiskLevel.High);
        }

        if (risk == ActionRiskLevel.Unknown)
        {
            confidence = AssessmentConfidence.Low;
            reasons.Add("No rule has enough evidence to classify this executable as low risk.");
            reasons.Add("Unknown shell behavior is treated conservatively rather than assumed safe.");
        }

        return new(risk, confidence, category, effect, resources, scope, destructive, reversible, elevation, credential, network, persistence, production, reasons, saferAlternative);
    }

    internal static IReadOnlyList<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        for (var i = 0; i < command.Length; i++)
        {
            var ch = command[i];
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else if (ch == '\\' && quote == '"' && i + 1 < command.Length) current.Append(command[++i]);
                else current.Append(ch);
                continue;
            }
            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                Flush();
                continue;
            }
            if (IsOperatorStart(ch))
            {
                Flush();
                var op = ch.ToString();
                if (i + 1 < command.Length && IsDoubleOperator(ch, command[i + 1])) op += command[++i];
                tokens.Add(op);
                continue;
            }
            if (ch == '$' && i + 1 < command.Length && command[i + 1] == '(')
            {
                Flush();
                tokens.Add("$(");
                i++;
                continue;
            }
            if (ch == '`')
            {
                Flush();
                tokens.Add("`");
                continue;
            }
            current.Append(ch);
        }
        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length == 0) return;
            tokens.Add(current.ToString());
            current.Clear();
        }
    }

    private static bool IsOperatorStart(char ch) => ch is ';' or '|' or '&' or '>' or '<';
    private static bool IsDoubleOperator(char first, char second) => (first == second && first is '|' or '&' or '>') || (first == '<' && second == '<');
    private static bool ContainsShellControlOperator(IReadOnlyList<string> tokens) => tokens.Any(x => x is ";" or "|" or "||" or "&&" or ">" or ">>" or "<" or "<<" or "$(" or "`");
    private static bool ContainsMutationRedirection(IReadOnlyList<string> tokens) => tokens.Any(x => x is ">" or ">>");
    private static bool ContainsWord(IEnumerable<string> tokens, string value) => tokens.Any(x => string.Equals(NormalizeExecutable(x), value, StringComparison.Ordinal));

    private static string NormalizeExecutable(string value)
    {
        value = value.Trim().Trim('"', '\'').Replace('\\', '/');
        var slash = value.LastIndexOf('/');
        if (slash >= 0) value = value[(slash + 1)..];
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        return value.ToLowerInvariant();
    }

    private static bool IsReadOnly(string first, string lower)
        => ReadOnlyCommands.Contains(first, StringComparer.Ordinal)
            || lower.StartsWith("git status", StringComparison.Ordinal)
            || lower.StartsWith("git diff", StringComparison.Ordinal)
            || lower.StartsWith("git log", StringComparison.Ordinal)
            || lower.StartsWith("git show", StringComparison.Ordinal);

    private static bool IsDeleteCommand(string first, string lower)
        => first is "rm" or "rmdir" or "del" or "erase" or "remove-item"
            || lower.StartsWith("rd ", StringComparison.Ordinal);

    private static bool IsDestructiveGit(string lower)
        => lower.StartsWith("git reset --hard", StringComparison.Ordinal)
            || lower.StartsWith("git clean", StringComparison.Ordinal) && HasAny(lower, " -f", "--force")
            || lower.StartsWith("git checkout --", StringComparison.Ordinal)
            || lower.StartsWith("git restore", StringComparison.Ordinal) && HasAny(lower, "--worktree", " .", " :/")
            || lower.StartsWith("git push", StringComparison.Ordinal) && HasAny(lower, "--force", " -f", "--delete");

    private static bool IsPackageMutation(string first, string lower)
        => first is "apt" or "apt-get" or "brew" or "winget" or "choco" or "scoop" or "npm" or "pnpm" or "yarn" or "pip" or "pip3" or "dotnet"
            && HasAny(lower, " install", " uninstall", " remove", " upgrade", " update", " add", " global");

    private static bool IsSystemMutation(string first, string lower)
        => first is "systemctl" or "service" or "sc" or "netsh" or "ufw" or "chmod" or "chown" or "reg" or "set-netfirewallrule" or "new-netfirewallrule" or "remove-netfirewallrule"
            || lower.StartsWith("set-service", StringComparison.Ordinal)
            || lower.StartsWith("start-service", StringComparison.Ordinal)
            || lower.StartsWith("stop-service", StringComparison.Ordinal);

    private static bool IsCatastrophic(string lower, string first)
        => first is "mkfs" or "diskpart" or "shutdown" or "reboot"
            || lower.Contains("rm -rf /", StringComparison.Ordinal)
            || lower.Contains("rm -fr /", StringComparison.Ordinal)
            || lower.Contains("format c:", StringComparison.Ordinal)
            || lower.Contains("dd if=", StringComparison.Ordinal) && lower.Contains(" of=/dev/", StringComparison.Ordinal);

    private static bool LooksNetworkRelated(string lower)
        => HasAny(lower, "http://", "https://", "ssh ", "scp ", "sftp ", "curl ", "wget ", "invoke-webrequest", "invoke-restmethod", "ftp ");

    private static bool HasAny(string value, params string[] fragments) => fragments.Any(x => value.Contains(x, StringComparison.Ordinal));

    private static ActionRiskLevel Max(ActionRiskLevel left, ActionRiskLevel right)
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
}
