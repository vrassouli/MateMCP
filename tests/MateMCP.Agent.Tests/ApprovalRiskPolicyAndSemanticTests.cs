using MateMCP.Agent.Security;

namespace MateMCP.Agent.Tests;

public sealed class ApprovalRiskPolicyAndSemanticTests
{
    [Fact]
    public void DefaultPolicy_AllowsStoredRulesOnlyForLowAndMedium()
    {
        var options = new ApprovalRiskPolicyOptions();

        var low = ApprovalRiskPolicyEvaluator.Evaluate(Assessment(ActionRiskLevel.Low), options);
        var medium = ApprovalRiskPolicyEvaluator.Evaluate(Assessment(ActionRiskLevel.Medium), options);
        var high = ApprovalRiskPolicyEvaluator.Evaluate(Assessment(ActionRiskLevel.High), options);
        var critical = ApprovalRiskPolicyEvaluator.Evaluate(Assessment(ActionRiskLevel.Critical), options);
        var unknown = ApprovalRiskPolicyEvaluator.Evaluate(Assessment(ActionRiskLevel.Unknown), options);

        Assert.Equal(ApprovalRiskBehavior.AllowStoredRule, low.Behavior);
        Assert.True(low.CanUseStoredRule);
        Assert.True(low.CanCreateBroadTrust);
        Assert.True(medium.CanUseStoredRule);

        Assert.Equal(ApprovalRiskBehavior.RequireApproval, high.Behavior);
        Assert.False(high.CanUseStoredRule);
        Assert.False(high.CanCreateBroadTrust);
        Assert.Equal(ApprovalRiskBehavior.RequireApproval, critical.Behavior);
        Assert.False(critical.CanUseStoredRule);
        Assert.False(critical.CanCreateBroadTrust);
        Assert.Equal(ApprovalRiskBehavior.RequireApproval, unknown.Behavior);
        Assert.False(unknown.CanUseStoredRule);
    }

    [Fact]
    public void Policy_CanExplicitlyDenyOrAutoAllowByRisk()
    {
        var options = new ApprovalRiskPolicyOptions
        {
            Low = ApprovalRiskBehavior.AutoAllow,
            Critical = ApprovalRiskBehavior.Deny
        };

        Assert.Equal(ApprovalRiskBehavior.AutoAllow,
            ApprovalRiskPolicyEvaluator.Evaluate(Assessment(ActionRiskLevel.Low), options).Behavior);
        Assert.Equal(ApprovalRiskBehavior.Deny,
            ApprovalRiskPolicyEvaluator.Evaluate(Assessment(ActionRiskLevel.Critical), options).Behavior);
    }

    [Fact]
    public void BroadTrust_ForHighRiskRequiresBothBehaviorAndExplicitRiskOptIn()
    {
        var options = new ApprovalRiskPolicyOptions
        {
            High = ApprovalRiskBehavior.AllowStoredRule,
            BroadTrustRisks = [ActionRiskLevel.Low, ActionRiskLevel.Medium, ActionRiskLevel.High]
        };

        var decision = ApprovalRiskPolicyEvaluator.Evaluate(Assessment(ActionRiskLevel.High), options);

        Assert.True(decision.CanUseStoredRule);
        Assert.True(decision.CanCreateBroadTrust);
    }

    [Fact]
    public void SecondarySemanticSignal_CannotLowerDeterministicRisk()
    {
        var deterministic = Assessment(ActionRiskLevel.Critical);
        var semantic = new SecondarySemanticSignal(ActionRiskLevel.Low, "Looks routine", null, "test-model");

        var result = ActionAnalysisService.ApplySemanticSignal(deterministic, semantic);

        Assert.Equal(ActionRiskLevel.Critical, result.Risk);
        Assert.Contains(result.Reasons, x => x.Contains("non-authoritative", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Contributors!, x => x.Source == "test-model" && !x.Authoritative);
    }

    [Fact]
    public void SecondarySemanticSignal_MayEscalateDeterministicRisk()
    {
        var deterministic = Assessment(ActionRiskLevel.Low);
        var semantic = new SecondarySemanticSignal(ActionRiskLevel.High, "The target appears externally reachable", "Review the endpoint first.", "test-model");

        var result = ActionAnalysisService.ApplySemanticSignal(deterministic, semantic);

        Assert.Equal(ActionRiskLevel.High, result.Risk);
        Assert.Equal("Review the endpoint first.", result.SaferAlternative);
    }

    [Fact]
    public async Task AnalyzerPack_ExtendsPipelineWithoutChangingCentralAnalyzer()
    {
        var service = new ActionAnalysisService([new TestPack()], new NoSemanticAnalyzer());
        var context = new ActionAssessmentContext("custom.destroy", "test", "custom action");

        var result = await service.AnalyzeAsync(context);

        Assert.Equal(ActionRiskLevel.Critical, result.Risk);
        Assert.Equal("custom-test", result.IntentCategory);
        Assert.Contains(result.Contributors!, x => x.Source == "test-pack" && x.Authoritative);
    }

    [Fact]
    public void AuditSummary_IncludesAnalyzerProvenanceWithoutContributorDetails()
    {
        var assessment = Assessment(ActionRiskLevel.High) with
        {
            Contributors =
            [
                new AssessmentContributor("builtin-deterministic", "deterministic", true, "private detail"),
                new AssessmentContributor("local-semantic:qwen", "semantic", false, "another detail")
            ]
        };

        var summary = assessment.ToAuditSummary();

        Assert.Contains("sources=builtin-deterministic,local-semantic:qwen", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("private detail", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("another detail", summary, StringComparison.Ordinal);
    }

    private static ActionImpactAssessment Assessment(ActionRiskLevel risk)
        => new(
            risk,
            AssessmentConfidence.High,
            "test",
            "test effect",
            [],
            "test",
            Destructive: risk is ActionRiskLevel.High or ActionRiskLevel.Critical,
            Reversible: ActionReversibility.Unknown,
            RequiresElevation: false,
            CredentialExposure: false,
            NetworkEffect: false,
            PersistenceEffect: false,
            ProductionLikelihood: false,
            Reasons: ["deterministic reason"],
            Contributors: [new AssessmentContributor("deterministic-test", "deterministic", true)]);

    private sealed class NoSemanticAnalyzer : ISecondarySemanticAnalyzer
    {
        public Task<SecondarySemanticSignal?> AnalyzeAsync(ActionAssessmentContext context, ActionImpactAssessment deterministicAssessment, CancellationToken cancellationToken = default)
            => Task.FromResult<SecondarySemanticSignal?>(null);
    }

    private sealed class TestPack : IActionImpactAnalyzerPack
    {
        public string Name => "test-pack";
        public int Priority => 100;
        public IEnumerable<IActionImpactAnalyzer> CreateAnalyzers() => [new TestAnalyzer()];
    }

    private sealed class TestAnalyzer : IActionImpactAnalyzer
    {
        public bool CanAnalyze(ActionAssessmentContext context) => context.Capability == "custom.destroy";

        public ActionImpactAssessment Analyze(ActionAssessmentContext context)
            => new(
                ActionRiskLevel.Critical,
                AssessmentConfidence.High,
                "custom-test",
                "The custom extension identified a critical action.",
                [context.Target],
                context.Target,
                Destructive: true,
                Reversible: ActionReversibility.No,
                RequiresElevation: false,
                CredentialExposure: false,
                NetworkEffect: false,
                PersistenceEffect: true,
                ProductionLikelihood: false,
                Reasons: ["Custom analyzer pack matched the action."]);
    }
}
