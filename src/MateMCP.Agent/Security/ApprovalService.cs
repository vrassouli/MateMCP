using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Desktop;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Security;

public enum ApprovalDecision { AllowOnce, AllowSession, AllowAlways, Deny, Timeout }

public sealed record PendingApproval(
    string Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string Capability,
    string Target,
    string Summary,
    string? Risk = null,
    string? Effect = null);

public sealed class ApprovalService(
    IOptionsMonitor<MateOptions> options,
    IHttpClientFactory clients,
    AgentCredentialStore credentials,
    ApprovalPolicyStore policies,
    AuditLog audit,
    LocalNotificationService notifications,
    ILogger<ApprovalService> logger) : IApprovalService
{
    private sealed class PendingState(PendingApproval approval)
    {
        public PendingApproval Approval { get; } = approval;
        public TaskCompletionSource<ApprovalDecision> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<string, PendingState> _pending = new(StringComparer.Ordinal);
    private MateOptions Current => options.CurrentValue;

    public IReadOnlyCollection<PendingApproval> GetPending() => _pending.Values.Select(x => x.Approval).OrderBy(x => x.CreatedAt).ToArray();
    public Task<IReadOnlyList<ApprovalPolicy>> GetPoliciesAsync(CancellationToken cancellationToken = default) => policies.GetAlwaysAsync(cancellationToken);
    public Task<bool> RemovePolicyAsync(string capability, string target, CancellationToken cancellationToken = default) => policies.RemoveAlwaysAsync(capability, target, cancellationToken);

    public Task<ApprovalDecision> RequestAsync(string capability, string target, string summary, CancellationToken cancellationToken)
        => RequestCoreAsync(capability, target, summary, assessment: null, cancellationToken);

    public Task<ApprovalDecision> RequestComputerUseAsync(
        string capability,
        string target,
        string summary,
        ComputerUseRiskAssessment assessment,
        CancellationToken cancellationToken)
        => RequestCoreAsync(capability, target, summary, assessment, cancellationToken);

    private async Task<ApprovalDecision> RequestCoreAsync(
        string capability,
        string target,
        string summary,
        ComputerUseRiskAssessment? assessment,
        CancellationToken cancellationToken)
    {
        var riskAudit = assessment is null ? string.Empty : $":risk={assessment.Label.ToLowerInvariant()}";
        if (policies.IsSessionAllowed(capability, target))
        {
            await audit.WriteAsync("approval", $"{capability}:{target}", $"allowed:session-policy{riskAudit}", cancellationToken);
            return ApprovalDecision.AllowSession;
        }
        if (await policies.IsAlwaysAllowedAsync(capability, target, cancellationToken))
        {
            await audit.WriteAsync("approval", $"{capability}:{target}", $"allowed:persistent-policy{riskAudit}", cancellationToken);
            return ApprovalDecision.AllowAlways;
        }

        var timeoutSeconds = Math.Clamp(Current.ApprovalTimeoutSeconds, 15, 600);
        var createdAt = DateTimeOffset.UtcNow;
        var approval = new PendingApproval(
            Guid.NewGuid().ToString("n"),
            createdAt,
            createdAt.AddSeconds(timeoutSeconds),
            capability,
            target,
            summary,
            assessment?.Label,
            assessment?.Effect);
        var state = new PendingState(approval);
        if (!_pending.TryAdd(approval.Id, state)) throw new InvalidOperationException("Failed to create approval request.");

        _ = PollRemoteDecisionAsync(state, cancellationToken);
        _ = notifications.NotifyApprovalAsync(Current.Port, approval, cancellationToken);
        logger.LogWarning("MateMCP approval required: {Capability} {Target} Risk={Risk}. Open http://127.0.0.1:{Port}/ui", capability, target, approval.Risk ?? "unspecified", Current.Port);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var decision = await state.Completion.Task.WaitAsync(timeout.Token);
            if (decision == ApprovalDecision.AllowSession) policies.AllowForSession(capability, target);
            if (decision == ApprovalDecision.AllowAlways) await policies.AllowAlwaysAsync(capability, target, cancellationToken);
            await audit.WriteAsync("approval", $"{capability}:{target}", $"decision:{decision.ToString().ToLowerInvariant()}{riskAudit}", cancellationToken);
            return decision;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await audit.WriteAsync("approval", $"{capability}:{target}", $"decision:timeout{riskAudit}", CancellationToken.None);
            return ApprovalDecision.Timeout;
        }
        finally { _pending.TryRemove(approval.Id, out _); }
    }

    public bool Decide(string id, ApprovalDecision decision)
    {
        if (!_pending.TryGetValue(id, out var state)) return false;
        return state.Completion.TrySetResult(decision);
    }

    private async Task PollRemoteDecisionAsync(PendingState state, CancellationToken cancellationToken)
    {
        var current = Current; var relay = current.Relay;
        if (!relay.Enabled || string.IsNullOrWhiteSpace(relay.DeviceId)) return;
        try
        {
            var credential = await credentials.GetAsync(relay.DeviceId, cancellationToken); if (credential is null) return;
            var client = clients.CreateClient(); client.BaseAddress = new Uri(relay.ControlPlaneUrl.TrimEnd('/') + "/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            var remoteSummary = state.Approval.Risk is null
                ? state.Approval.Summary
                : $"{state.Approval.Summary}\nRisk: {state.Approval.Risk}\nExpected effect: {state.Approval.Effect}";
            using var created = await client.PostAsJsonAsync(
                $"api/agents/{Uri.EscapeDataString(relay.DeviceId)}/approvals",
                new { state.Approval.Capability, state.Approval.Target, Summary = remoteSummary, ExpiresIn = current.ApprovalTimeoutSeconds },
                cancellationToken);
            if (!created.IsSuccessStatusCode) { logger.LogWarning("Remote approval publication failed with {StatusCode}.", created.StatusCode); return; }
            var remote = await created.Content.ReadFromJsonAsync<RemoteApproval>(cancellationToken: cancellationToken); if (remote is null) return;
            var delay = TimeSpan.FromSeconds(2);
            while (!state.Completion.Task.IsCompleted && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(delay, cancellationToken);
                var decision = await client.GetFromJsonAsync<RemoteDecision>($"api/agents/{Uri.EscapeDataString(relay.DeviceId)}/approvals/{remote.Id}", cancellationToken);
                if (decision?.Status == "allowed") { state.Completion.TrySetResult(ApprovalDecision.AllowOnce); return; }
                if (decision?.Status == "denied") { state.Completion.TrySetResult(ApprovalDecision.Deny); return; }
                if (decision?.Status == "expired") { state.Completion.TrySetResult(ApprovalDecision.Timeout); return; }
                delay = TimeSpan.FromSeconds(Math.Min(8, delay.TotalSeconds * 1.6));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning(ex, "Remote approval channel unavailable; local approval remains active."); }
    }

    private sealed record RemoteApproval(Guid Id);
    private sealed record RemoteDecision(string Status);
}
