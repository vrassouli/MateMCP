using System.Text.RegularExpressions;
using MateMCP.Agent.Diagnostics;

namespace MateMCP.Agent.Security;

public enum ActionRiskLevel
{
    Low,
    Medium,
    High,
    Critical,
    Unknown
}

public enum AssessmentConfidence
{
    Low,
    Medium,
    High
}

public enum ActionReversibility
{
    Yes,
    Partial,
    No,
    Unknown
}

public sealed record ActionAssessmentContext(
    string Capability,
    string Target,
    string Summary,
    string? ActionType = null,
    IReadOnlyDictionary<string, string?>? Arguments = null);

public sealed record AssessmentContributor(
    string Source,
    string Kind,
    bool Authoritative,
    string? Detail = null);

public sealed record ActionImpactAssessment(
    ActionRiskLevel Risk,
    AssessmentConfidence Confidence,
    string IntentCategory,
    string Effect,
    IReadOnlyList<string> AffectedResources,
    string Scope,
    bool Destructive,
    ActionReversibility Reversible,
    bool RequiresElevation,
    bool CredentialExposure,
    bool NetworkEffect,
    bool PersistenceEffect,
    bool ProductionLikelihood,
    IReadOnlyList<string> Reasons,
    string? SaferAlternative = null,
    string? Preview = null,
    IReadOnlyList<AssessmentContributor>? Contributors = null)
{
    private static readonly Regex SensitiveArgumentPattern = new(
        "(?i)((?:--?|/)(?:password|passwd|token|access[-_]?token|refresh[-_]?token|api[-_]?key|client[-_]?secret|secret)\\s+)(?:\"[^\"]*\"|'[^']*'|\\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string RiskLabel => Risk.ToString();
    public string ConfidenceLabel => Confidence.ToString();
    public string ReversibilityLabel => Reversible.ToString();

    public string ToAuditSummary()
    {
        var sources = Contributors is null || Contributors.Count == 0
            ? "unspecified"
            : string.Join(",", Contributors.Select(x => Bound(x.Source, 48)).Distinct(StringComparer.Ordinal).Take(8));
        return $"risk={Risk.ToString().ToLowerInvariant()};confidence={Confidence.ToString().ToLowerInvariant()};category={Bound(IntentCategory, 48)};destructive={Destructive.ToString().ToLowerInvariant()};reversible={Reversible.ToString().ToLowerInvariant()};elevation={RequiresElevation.ToString().ToLowerInvariant()};credential={CredentialExposure.ToString().ToLowerInvariant()};network={NetworkEffect.ToString().ToLowerInvariant()};persistence={PersistenceEffect.ToString().ToLowerInvariant()};sources={sources}";
    }

    public string ToRemoteSummary(string originalSummary)
    {
        var reasons = Reasons.Count == 0 ? "No additional deterministic reasons." : string.Join("; ", Reasons.Take(4).Select(x => Bound(x, 180)));
        var resources = AffectedResources.Count == 0 ? "unspecified" : string.Join(", ", AffectedResources.Take(4).Select(x => Bound(x, 120)));
        var safeSummary = RedactRemoteSummary(originalSummary);
        return $"{Bound(safeSummary, 1200)}\nRisk: {RiskLabel} ({ConfidenceLabel} confidence)\nExpected effect: {Bound(Effect, 500)}\nReversibility: {ReversibilityLabel}\nAffected: {resources}\nWhy: {reasons}";
    }

    internal static string RedactRemoteSummary(string value)
    {
        var redacted = AgentLogRedactor.Redact(value);
        return SensitiveArgumentPattern.Replace(redacted, "$1[REDACTED]");
    }

    internal static string Bound(string? value, int max)
    {
        value ??= string.Empty;
        return value.Length <= max ? value : value[..max] + "…";
    }
}

public interface IActionImpactAnalyzer
{
    bool CanAnalyze(ActionAssessmentContext context);
    ActionImpactAssessment Analyze(ActionAssessmentContext context);
}

public interface IActionImpactAnalyzerPack
{
    string Name { get; }
    int Priority { get; }
    IEnumerable<IActionImpactAnalyzer> CreateAnalyzers();
}
