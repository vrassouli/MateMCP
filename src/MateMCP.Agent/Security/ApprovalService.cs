using System.Collections.Concurrent;
using MateMCP.Agent.Configuration;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace MateMCP.Agent.Security;

public sealed record PendingApproval(
    string Id,
    DateTimeOffset CreatedAt,
    string Capability,
    string Target,
    string Summary);

public sealed class ApprovalService(IOptionsMonitor<MateOptions> options, IHttpClientFactory clients, AgentCredentialStore credentials, ILogger<ApprovalService> logger)
{
    private sealed class PendingState(PendingApproval approval)
    {
        public PendingApproval Approval { get; } = approval;
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<string, PendingState> _pending = new(StringComparer.Ordinal);
    private MateOptions Current => options.CurrentValue;

    public IReadOnlyCollection<PendingApproval> GetPending() =>
        _pending.Values.Select(x => x.Approval).OrderBy(x => x.CreatedAt).ToArray();

    public async Task<bool> RequestAsync(string capability, string target, string summary, CancellationToken cancellationToken)
    {
        var approval = new PendingApproval(
            Guid.NewGuid().ToString("n"),
            DateTimeOffset.UtcNow,
            capability,
            target,
            summary);
        var state = new PendingState(approval);
        if (!_pending.TryAdd(approval.Id, state))
            throw new InvalidOperationException("Failed to create approval request.");

        _ = PollRemoteDecisionAsync(state, cancellationToken);

        logger.LogWarning("MateMCP approval required: {Capability} {Target}. Open http://127.0.0.1:{Port}/approvals", capability, target, Current.Port);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(Current.ApprovalTimeoutSeconds, 15, 600)));
        try
        {
            return await state.Completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _pending.TryRemove(approval.Id, out _);
        }
    }

    public bool Decide(string id, bool allow)
    {
        if (!_pending.TryGetValue(id, out var state)) return false;
        return state.Completion.TrySetResult(allow);
    }

    private async Task PollRemoteDecisionAsync(PendingState state, CancellationToken cancellationToken)
    {
        var current = Current;
        var relay = current.Relay;
        if (!relay.Enabled || string.IsNullOrWhiteSpace(relay.DeviceId)) return;
        try
        {
            var credential = await credentials.GetAsync(relay.DeviceId, cancellationToken); if (credential is null) return;
            var client = clients.CreateClient(); client.BaseAddress = new Uri(relay.ControlPlaneUrl.TrimEnd('/') + "/"); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            using var created = await client.PostAsJsonAsync($"api/agents/{Uri.EscapeDataString(relay.DeviceId)}/approvals", new { state.Approval.Capability, state.Approval.Target, state.Approval.Summary, ExpiresIn = current.ApprovalTimeoutSeconds }, cancellationToken);
            if (!created.IsSuccessStatusCode) { logger.LogWarning("Remote approval publication failed with {StatusCode}.", created.StatusCode); return; }
            var remote = await created.Content.ReadFromJsonAsync<RemoteApproval>(cancellationToken: cancellationToken); if (remote is null) return;
            while (!state.Completion.Task.IsCompleted && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                var decision = await client.GetFromJsonAsync<RemoteDecision>($"api/agents/{Uri.EscapeDataString(relay.DeviceId)}/approvals/{remote.Id}", cancellationToken);
                if (decision?.Status == "allowed") { state.Completion.TrySetResult(true); return; }
                if (decision?.Status is "denied" or "expired") { state.Completion.TrySetResult(false); return; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning(ex, "Remote approval channel unavailable; local approval remains active."); }
    }

    private sealed record RemoteApproval(Guid Id);
    private sealed record RemoteDecision(string Status);
}
