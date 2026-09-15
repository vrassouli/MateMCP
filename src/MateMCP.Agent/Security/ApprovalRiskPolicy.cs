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
    /// Retained for configuration compatibility. Persistent/session authorization is now a user trust decision
    /// for every non-denied approval, including high/critical risk. Risk still controls whether an approval is
    /// initially required and how prominently the operation is explained.
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

        // A hard deny remains a deny. Otherwise, if the operation can execute at all, the user may choose
        // session-scoped or persistent trust for it. RequireApproval means "ask unless trusted", not
        // "persistent trust is forbidden".
        var canUseStoredRule = behavior is ApprovalRiskBehavior.AllowStoredRule or ApprovalRiskBehavior.RequireApproval;
        var canCreateBroadTrust = behavior != ApprovalRiskBehavior.Deny;

        return new(
            behavior,
            CanUseStoredRule: canUseStoredRule,
            CanCreateBroadTrust: canCreateBroadTrust,
            Reason: $"Risk policy for {risk}: {behavior}; stored trust {(canUseStoredRule ? "eligible" : "not used")}; persistent trust {(canCreateBroadTrust ? "allowed" : "denied")}.");
    }
}
