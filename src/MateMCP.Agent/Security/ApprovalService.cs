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
    string? Effect = null,
    string? Confidence = null,
    string? IntentCategory = null,
    string? Reversibility = null,
    bool Destructive = false,
    bool RequiresElevation = false,
    bool CredentialExposure = false,
    bool NetworkEffect = false,
    bool PersistenceEffect = false,
    IReadOnlyList<string>? AffectedResources = null,
    IReadOnlyList<string>? Reasons = null,
    string? SaferAlternative = null,
    string? Preview = null,
    bool CanAllowSession = true,
    bool CanAllowAlways = true,
    IReadOnlyList<AssessmentContributor>? Contributors = null);

public sealed class ApprovalService(
    IOptionsMonitor<MateOptions> options,
    IHttpClientFactory clients,
    AgentCredentialStore credentials,
    ApprovalPolicyStore policies,
    AuditLog audit,
    LocalNotificationService notifications,
    AgentAccessModeStore accessModes,
    ILogger<ApprovalService> logger) : IApprovalService
{
    private sealed class PendingState(PendingApproval approval, ActionImpactAssessment? assessment)
    {
        public PendingApproval Approval { get; } = approval;
        public ActionImpactAssessment? Assessment { get; } = assessment;
        public TaskCompletionSource<ApprovalDecision> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<string, PendingState> _pending = new(StringComparer.Ordinal);
    private readonly AgentAccessModeStore _accessModes = accessModes;
    private readonly ActionAnalysisService _analysis = new(
        ActionImpactAnalyzerPacks.Snapshot(),
        new LocalOpenAiCompatibleSemanticAnalyzer(clients, options, logger),
        logger);
    private MateOptions Current => options.CurrentValue;

    public IReadOnlyCollection<PendingApproval> GetPending() => _pending.Values.Select(x => x.Approval).OrderBy(x => x.CreatedAt).ToArray();
    public Task<IReadOnlyList<ApprovalPolicy>> GetPoliciesAsync(CancellationToken cancellationToken = default) => policies.GetAlwaysAsync(cancellationToken);
    public Task<bool> RemovePolicyAsync(string capability, string target, CancellationToken cancellationToken = default) => policies.RemoveAlwaysAsync(capability, target, cancellationToken);

    public async Task<ApprovalDecision> RequestAsync(string capability, string target, string summary, CancellationToken cancellationToken)
    {
        var context = new ActionAssessmentContext(capability, target, summary);
        var assessment = await _analysis.AnalyzeAsync(context, cancellationToken);
        return await RequestCoreAsync(capability, target, summary, assessment, cancellationToken);
    }

    public async Task<ApprovalDecision> RequestAsync(ActionAssessmentContext context, CancellationToken cancellationToken)
    {
        var assessment = await _analysis.AnalyzeAsync(context, cancellationToken);
        return await RequestCoreAsync(context.Capability, context.Target, context.Summary, assessment, cancellationToken);
    }

    public Task<ApprovalDecision> RequestComputerUseAsync(
        string capability,
        string target,
        string summary,
        ComputerUseRiskAssessment assessment,
        CancellationToken cancellationToken)
        => RequestCoreAsync(capability, target, summary, FromComputerUse(target, assessment), cancellationToken);

    private async Task<ApprovalDecision> RequestCoreAsync(
        string capability,
        string target,
        string summary,
        ActionImpactAssessment? assessment,
        CancellationToken cancellationToken)
    {
        var assessmentAudit = assessment is null ? string.Empty : ":" + assessment.ToAuditSummary();
        var riskPolicy = ApprovalRiskPolicyEvaluator.Evaluate(assessment, Current.ApprovalRiskPolicy);
        var riskPolicyAudit = $":policy={riskPolicy.Behavior.ToString().ToLowerInvariant()}";

        // Store a bounded/redacted copy of the proposed action plus the full structured assessment.
        // Decision events remain separate for backwards-compatible audit timelines.
        await audit.WriteApprovalAsync(
            "approval.assessment",
            $"{capability}:{target}",
            summary,
            $"analyzed{riskPolicyAudit}{assessmentAudit}",
            assessment,
            cancellationToken);

        var accessMode = await _accessModes.GetAsync(cancellationToken);
        if (accessMode.Mode == AgentAccessMode.FullAccess)
        {
            await audit.WriteAsync(
                "approval",
                $"{capability}:{target}",
                $"allowed:full-access{riskPolicyAudit}{assessmentAudit}",
                cancellationToken);
            logger.LogWarning(
                "MateMCP Full Access allowed {Capability} {Target} without an interactive approval.",
                capability,
                target);
            return ApprovalDecision.AllowAlways;
        }

        if (riskPolicy.Behavior == ApprovalRiskBehavior.Deny)
        {
            await audit.WriteAsync("approval", $"{capability}:{target}", $"denied:risk-policy{riskPolicyAudit}{assessmentAudit}", cancellationToken);
            logger.LogWarning("MateMCP risk policy denied {Capability} {Target} Risk={Risk}.", capability, target, assessment?.RiskLabel ?? "Unknown");
            return ApprovalDecision.Deny;
        }

        if (riskPolicy.Behavior == ApprovalRiskBehavior.AutoAllow)
        {
            await audit.WriteAsync("approval", $"{capability}:{target}", $"allowed:risk-policy-auto{riskPolicyAudit}{assessmentAudit}", cancellationToken);
            return ApprovalDecision.AllowOnce;
        }

        if (riskPolicy.CanUseStoredRule && policies.IsSessionAllowed(capability, target))
        {
            await audit.WriteAsync("approval", $"{capability}:{target}", $"allowed:session-policy{riskPolicyAudit}{assessmentAudit}", cancellationToken);
            return ApprovalDecision.AllowSession;
        }
        if (riskPolicy.CanUseStoredRule && await policies.IsAlwaysAllowedAsync(capability, target, cancellationToken))
        {
            await audit.WriteAsync("approval", $"{capability}:{target}", $"allowed:persistent-policy{riskPolicyAudit}{assessmentAudit}", cancellationToken);
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
            assessment?.RiskLabel,
            assessment?.Effect,
            assessment?.ConfidenceLabel,
            assessment?.IntentCategory,
            assessment?.ReversibilityLabel,
            assessment?.Destructive ?? false,
            assessment?.RequiresElevation ?? false,
            assessment?.CredentialExposure ?? false,
            assessment?.NetworkEffect ?? false,
            assessment?.PersistenceEffect ?? false,
            assessment?.AffectedResources,
            assessment?.Reasons,
            assessment?.SaferAlternative,
            assessment?.Preview,
            riskPolicy.CanCreateBroadTrust,
            riskPolicy.CanCreateBroadTrust,
            assessment?.Contributors);
        var state = new PendingState(approval, assessment);
        if (!_pending.TryAdd(approval.Id, state)) throw new InvalidOperationException("Failed to create approval request.");

        _ = PollRemoteDecisionAsync(state, cancellationToken);
        _ = notifications.NotifyApprovalAsync(Current.Port, approval, cancellationToken);
        logger.LogWarning(
            "MateMCP approval required: {Capability} {Target} Risk={Risk} Confidence={Confidence} Category={Category} Policy={Policy}. Open http://127.0.0.1:{Port}/ui",
            capability,
            target,
            approval.Risk ?? "Unknown",
            approval.Confidence ?? "Low",
            approval.IntentCategory ?? "unknown",
            riskPolicy.Behavior,
            Current.Port);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            var decision = await state.Completion.Task.WaitAsync(timeout.Token);
            if (decision == ApprovalDecision.AllowSession && approval.CanAllowSession) policies.AllowForSession(capability, target);
            if (decision == ApprovalDecision.AllowAlways && approval.CanAllowAlways) await policies.AllowAlwaysAsync(capability, target, cancellationToken);
            await audit.WriteAsync("approval", $"{capability}:{target}", $"decision:{decision.ToString().ToLowerInvariant()}{riskPolicyAudit}{assessmentAudit}", cancellationToken);
            return decision;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await audit.WriteAsync("approval", $"{capability}:{target}", $"decision:timeout{riskPolicyAudit}{assessmentAudit}", CancellationToken.None);
            return ApprovalDecision.Timeout;
        }
        finally { _pending.TryRemove(approval.Id, out _); }
    }

    public bool Decide(string id, ApprovalDecision decision)
    {
        if (!_pending.TryGetValue(id, out var state)) return false;
        if (decision == ApprovalDecision.AllowSession && !state.Approval.CanAllowSession) return false;
        if (decision == ApprovalDecision.AllowAlways && !state.Approval.CanAllowAlways) return false;
        if (!state.Completion.TrySetResult(decision)) return false;

        // Remove synchronously so Companion refreshes cannot observe a request that was already decided.
        // RequestCore also removes in its finally block, making this safe against timeout/cancellation races.
        _pending.TryRemove(id, out _);
        return true;
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
            var remoteSummary = state.Assessment is null
                ? state.Approval.Summary
                : state.Assessment.ToRemoteSummary(state.Approval.Summary);
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

    private static ActionImpactAssessment FromComputerUse(string target, ComputerUseRiskAssessment assessment)
    {
        var risk = assessment.Level switch
        {
            ComputerUseRiskLevel.Low => ActionRiskLevel.Low,
            ComputerUseRiskLevel.Sensitive => ActionRiskLevel.Medium,
            ComputerUseRiskLevel.High => ActionRiskLevel.High,
            _ => ActionRiskLevel.Unknown
        };
        return new(
            risk,
            AssessmentConfidence.Medium,
            "computer-use",
            assessment.Effect,
            string.IsNullOrWhiteSpace(target) ? [] : [target],
            string.IsNullOrWhiteSpace(target) ? "unspecified" : target,
            Destructive: assessment.Level == ComputerUseRiskLevel.High,
            Reversible: ActionReversibility.Unknown,
            RequiresElevation: false,
            CredentialExposure: assessment.Effect.Contains("credential", StringComparison.OrdinalIgnoreCase) || assessment.Effect.Contains("secret", StringComparison.OrdinalIgnoreCase),
            NetworkEffect: true,
            PersistenceEffect: assessment.Level != ComputerUseRiskLevel.Low,
            ProductionLikelihood: ActionImpactAnalyzer.LooksProductionRelated(target),
            Reasons: [assessment.Reason],
            Contributors: [new AssessmentContributor("computer-use-risk", "deterministic", Authoritative: true)]);
    }

    private sealed record RemoteApproval(Guid Id);
    private sealed record RemoteDecision(string Status);
}
