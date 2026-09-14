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
    string? Preview = null)
{
    public string RiskLabel => Risk.ToString();
    public string ConfidenceLabel => Confidence.ToString();
    public string ReversibilityLabel => Reversible.ToString();

    public string ToAuditSummary()
        => $"risk={Risk.ToString().ToLowerInvariant()};confidence={Confidence.ToString().ToLowerInvariant()};category={Bound(IntentCategory, 48)};destructive={Destructive.ToString().ToLowerInvariant()};reversible={Reversible.ToString().ToLowerInvariant()};elevation={RequiresElevation.ToString().ToLowerInvariant()};credential={CredentialExposure.ToString().ToLowerInvariant()};network={NetworkEffect.ToString().ToLowerInvariant()};persistence={PersistenceEffect.ToString().ToLowerInvariant()}";

    public string ToRemoteSummary(string originalSummary)
    {
        var reasons = Reasons.Count == 0 ? "No additional deterministic reasons." : string.Join("; ", Reasons.Take(4).Select(x => Bound(x, 180)));
        var resources = AffectedResources.Count == 0 ? "unspecified" : string.Join(", ", AffectedResources.Take(4).Select(x => Bound(x, 120)));
        return $"{Bound(originalSummary, 1200)}\nRisk: {RiskLabel} ({ConfidenceLabel} confidence)\nExpected effect: {Bound(Effect, 500)}\nReversibility: {ReversibilityLabel}\nAffected: {resources}\nWhy: {reasons}";
    }

    private static string Bound(string? value, int max)
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
