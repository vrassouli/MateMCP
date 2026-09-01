namespace MateMCP.Agent.Security;

public interface IApprovalService
{
    Task<ApprovalDecision> RequestAsync(string capability, string target, string summary, CancellationToken cancellationToken);
}
