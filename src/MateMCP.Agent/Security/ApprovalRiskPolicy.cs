using MateMCP.Agent.Configuration;

namespace MateMCP.Agent.Security;

public enum ApprovalRiskBehavior
{
    AutoAllow,
    AllowStoredRule,
    RequireApproval,
    Deny
}

public sealed class ApprovalRiskPolicyOptions
{
    public ApprovalRiskBehavior Low { get; set; } = ApprovalRiskBehavior.AllowStoredRule;
    public ApprovalRiskBehavior Medium { get; set; } = ApprovalRiskBehavior.AllowStoredRule;
    public ApprovalRiskBehavior High { get; set; } = ApprovalRiskBehavior.RequireApproval;
    public ApprovalRiskBehavior Critical { get; set; } = ApprovalRiskBehavior.RequireApproval;
    public ApprovalRiskBehavior Unknown { get; set; } = ApprovalRiskBehavior.RequireApproval;

    /// <summary>
    /// Risk levels for which a user-created session/persistent trust rule may be reused or created.
    /// Critical/Unknown are intentionally excluded by default.
    /// </summary>
    public List<ActionRiskLevel> BroadTrustRisks { get; set; } = [ActionRiskLevel.Low, ActionRiskLevel.Medium];
}

public sealed record ApprovalRiskPolicyDecision(
    ApprovalRiskBehavior Behavior,
    bool CanUseStoredRule,
    bool CanCreateBroadTrust,
    string Reason);

public static class ApprovalRiskPolicyEvaluator
{
    public static ApprovalRiskPolicyDecision Evaluate(ActionImpactAssessment? assessment, ApprovalRiskPolicyOptions? options)
    {
        options ??= new ApprovalRiskPolicyOptions();
        var risk = assessment?.Risk ?? ActionRiskLevel.Unknown;
        var behavior = risk switch
        {
            ActionRiskLevel.Low => options.Low,
            ActionRiskLevel.Medium => options.Medium,
            ActionRiskLevel.High => options.High,
            ActionRiskLevel.Critical => options.Critical,
            _ => options.Unknown
        };
        var broadTrust = options.BroadTrustRisks?.Contains(risk) == true;
        return new(
            behavior,
            CanUseStoredRule: behavior == ApprovalRiskBehavior.AllowStoredRule && broadTrust,
            CanCreateBroadTrust: broadTrust && behavior is not ApprovalRiskBehavior.Deny,
            Reason: $"Risk policy for {risk}: {behavior}; broad trust {(broadTrust ? "allowed" : "not allowed")}.");
    }
}
