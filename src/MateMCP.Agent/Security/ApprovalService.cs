using System.Collections.Concurrent;
using MateMCP.Agent.Configuration;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Security;

public sealed record PendingApproval(
    string Id,
    DateTimeOffset CreatedAt,
    string Capability,
    string Target,
    string Summary);

public sealed class ApprovalService(IOptions<MateOptions> options, ILogger<ApprovalService> logger)
{
    private sealed class PendingState(PendingApproval approval)
    {
        public PendingApproval Approval { get; } = approval;
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<string, PendingState> _pending = new(StringComparer.Ordinal);
    private readonly MateOptions _options = options.Value;

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

        logger.LogWarning("MateMCP approval required: {Capability} {Target}. Open http://127.0.0.1:{Port}/approvals", capability, target, _options.Port);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.ApprovalTimeoutSeconds, 15, 600)));
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
}
