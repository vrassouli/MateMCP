namespace MateMCP.Agent.Security;

public interface IApprovalService
{
    Task<ApprovalDecision> RequestAsync(string capability, string target, string summary, CancellationToken cancellationToken);

    Task<ApprovalDecision> RequestAsync(ActionAssessmentContext context, CancellationToken cancellationToken)
        => RequestAsync(context.Capability, context.Target, context.Summary, cancellationToken);
}
