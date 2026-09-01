namespace MateMCP.Agent.Companion.Services;

public sealed class ApprovalNotificationWatcher(AgentApiClient api, NativeApprovalNotifier notifier) : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly HashSet<string> _notified = new(StringComparer.Ordinal);
    private Task? _worker;

    public void Start()
    {
        _worker ??= Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try { await notifier.InitializeAsync(); }
        catch { /* Companion UI remains usable even if OS notifications are unavailable. */ }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                var approvals = await api.GetApprovalsAsync(ct);
                var active = approvals.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
                _notified.RemoveWhere(id => !active.Contains(id));

                foreach (var approval in approvals)
                {
                    if (!_notified.Add(approval.Id)) continue;
                    try { await notifier.ShowAsync(approval); }
                    catch { /* Keep polling; the in-app approval center is the fallback. */ }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { /* Agent may be temporarily unavailable during startup/restart. */ }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_worker is not null)
        {
            try { await _worker; } catch (OperationCanceledException) { }
        }
        notifier.Dispose();
        _cts.Dispose();
    }
}
