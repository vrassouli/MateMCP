using System.Collections.Concurrent;

namespace MateMCP.Agent.Security;

/// <summary>
/// Registration point for optional OS/shell/tool rule packs. Packs can be registered by an
/// integration without modifying the central analyzer implementation.
/// </summary>
public static class ActionImpactAnalyzerPacks
{
    private static readonly ConcurrentDictionary<string, IActionImpactAnalyzerPack> Packs = new(StringComparer.Ordinal);

    public static void Register(IActionImpactAnalyzerPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (string.IsNullOrWhiteSpace(pack.Name)) throw new ArgumentException("Analyzer pack name is required.", nameof(pack));
        Packs[pack.Name] = pack;
    }

    public static bool Unregister(string name) => Packs.TryRemove(name, out _);

    public static IReadOnlyList<IActionImpactAnalyzerPack> Snapshot()
        => Packs.Values.OrderByDescending(x => x.Priority).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
}

/// <summary>
/// Composes authoritative deterministic rules, read-only preflight, extension packs, and an
/// optional non-authoritative semantic signal into one assessment pipeline.
/// </summary>
public sealed class ActionAnalysisService
{
    private readonly IReadOnlyList<(IActionImpactAnalyzerPack Pack, IActionImpactAnalyzer Analyzer)> _extensions;
    private readonly ISecondarySemanticAnalyzer _semantic;
    private readonly ILogger? _logger;

    public ActionAnalysisService(
        IEnumerable<IActionImpactAnalyzerPack> packs,
        ISecondarySemanticAnalyzer semantic,
        ILogger? logger = null)
    {
        _extensions = packs
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .SelectMany(pack => pack.CreateAnalyzers().Select(analyzer => (pack, analyzer)))
            .ToArray();
        _semantic = semantic;
        _logger = logger;
    }

    public async Task<ActionImpactAssessment> AnalyzeAsync(
        ActionAssessmentContext context,
        CancellationToken cancellationToken = default)
    {
        var assessment = AnalyzeDeterministically(context);
        var preflight = await ActionContextPreflightAnalyzer.Default.EnrichAsync(context, assessment, cancellationToken);
        if (!ReferenceEquals(preflight, assessment))
        {
            assessment = AddContributor(preflight, new AssessmentContributor(
                "context-preflight",
                "deterministic-preflight",
                Authoritative: true,
                Detail: "Bounded local read-only inspection"));
        }
        else
        {
            assessment = preflight;
        }

        SecondarySemanticSignal? signal = null;
        try { signal = await _semantic.AnalyzeAsync(context, assessment, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger?.LogInformation(ex, "Secondary semantic analyzer failed; deterministic assessment remains authoritative.");
        }

        return signal is null ? assessment : ApplySemanticSignal(assessment, signal);
    }

    private ActionImpactAssessment AnalyzeDeterministically(ActionAssessmentContext context)
    {
        foreach (var (pack, analyzer) in _extensions)
        {
            try
            {
                if (!analyzer.CanAnalyze(context)) continue;
                var result = analyzer.Analyze(context);
                return AddContributor(result, new AssessmentContributor(
                    pack.Name,
                    "deterministic-extension",
                    Authoritative: true,
                    Detail: analyzer.GetType().Name));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Action analyzer extension {Pack}/{Analyzer} failed; falling back to built-in deterministic analysis.", pack.Name, analyzer.GetType().Name);
            }
        }

        return AddContributor(ActionImpactAnalyzer.Default.Assess(context), new AssessmentContributor(
            "builtin-deterministic",
            "deterministic",
            Authoritative: true));
    }

    internal static ActionImpactAssessment ApplySemanticSignal(ActionImpactAssessment deterministic, SecondarySemanticSignal signal)
    {
        var risk = MaxRisk(deterministic.Risk, signal.Risk);
        var reasons = deterministic.Reasons.ToList();
        reasons.Add($"Secondary local semantic signal (non-authoritative) suggested {signal.Risk}: {signal.Reason}");
        var safer = deterministic.SaferAlternative ?? signal.SaferAlternative;
        var result = deterministic with
        {
            Risk = risk,
            Reasons = reasons.Distinct(StringComparer.Ordinal).Take(12).ToArray(),
            SaferAlternative = safer
        };
        return AddContributor(result, new AssessmentContributor(
            signal.Source,
            "semantic",
            Authoritative: false,
            Detail: "May escalate risk or add context; cannot reduce deterministic risk"));
    }

    private static ActionImpactAssessment AddContributor(ActionImpactAssessment assessment, AssessmentContributor contributor)
    {
        var contributors = (assessment.Contributors ?? []).ToList();
        if (!contributors.Any(x => string.Equals(x.Source, contributor.Source, StringComparison.Ordinal)
            && string.Equals(x.Kind, contributor.Kind, StringComparison.Ordinal)))
            contributors.Add(contributor);
        return assessment with { Contributors = contributors.Take(12).ToArray() };
    }

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
}
