using MateMCP.Agent.Tools;

namespace MateMCP.Agent.Desktop;

public sealed record AgentPowerStatus(
    bool PreventSleepWhileInUse,
    bool Supported,
    bool InUse,
    bool SleepPrevented,
    string Message,
    string? LastError = null);

public sealed class PowerInhibitionCoordinator(IPowerInhibitor inhibitor)
{
    public static readonly TimeSpan IdleGracePeriod = TimeSpan.FromMinutes(15);

    public bool IsActive => inhibitor.IsActive;

    public static bool IsWithinIdleGrace(DateTimeOffset? lastActivityAt, DateTimeOffset now)
        => lastActivityAt is { } last
           && now >= last
           && now - last < IdleGracePeriod;

    public void Reconcile(bool enabled, bool inUse)
        => Reconcile(enabled, inUse, lastActivityAt: null, DateTimeOffset.UtcNow);

    public void Reconcile(bool enabled, bool inUse, DateTimeOffset? lastActivityAt, DateTimeOffset now)
    {
        if (enabled && (inUse || IsWithinIdleGrace(lastActivityAt, now)))
        {
            inhibitor.Acquire();
            return;
        }

        inhibitor.Release();
    }

    public void Release() => inhibitor.Release();
}

public sealed class AgentPowerInhibitionService(
    AgentPowerSettingsStore settings,
    IPowerInhibitor inhibitor,
    AgentActivityGate activity,
    InteractiveShellSessionManager sessions,
    ILogger<AgentPowerInhibitionService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _reconcile = new(1, 1);
    private readonly PowerInhibitionCoordinator _coordinator = new(inhibitor);
    private long _lastObservedInUseUnixMilliseconds;

    private readonly record struct UsageState(
        DateTimeOffset Now,
        bool InUse,
        bool InGracePeriod,
        DateTimeOffset? LastActivityAt)
    {
        public bool ShouldPreventSleep => InUse || InGracePeriod;
    }

    public void RequestReconcile()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    public async Task<AgentPowerStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var current = await settings.GetAsync(ct);
        var usage = GetUsageState();
        await ReconcileAsync(current.PreventSleepWhileInUse, usage, ct);
        return BuildStatus(current.PreventSleepWhileInUse, usage);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var current = await settings.GetAsync(stoppingToken);
                var usage = GetUsageState();
                await ReconcileAsync(current.PreventSleepWhileInUse, usage, stoppingToken);
                await _wake.WaitAsync(TimeSpan.FromMilliseconds(500), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _coordinator.Release();
        }
    }

    private UsageState GetUsageState()
    {
        var now = DateTimeOffset.UtcNow;
        var inUse = activity.IsActive || sessions.ActiveSessionCount > 0;
        if (inUse)
            Interlocked.Exchange(ref _lastObservedInUseUnixMilliseconds, now.ToUnixTimeMilliseconds());

        var observedUnixMilliseconds = Volatile.Read(ref _lastObservedInUseUnixMilliseconds);
        var observedAt = observedUnixMilliseconds <= 0
            ? (DateTimeOffset?)null
            : DateTimeOffset.FromUnixTimeMilliseconds(observedUnixMilliseconds);
        var lastActivityAt = Latest(activity.LastActivityAt, observedAt);
        var inGracePeriod = !inUse && PowerInhibitionCoordinator.IsWithinIdleGrace(lastActivityAt, now);
        return new UsageState(now, inUse, inGracePeriod, lastActivityAt);
    }

    private async Task ReconcileAsync(bool enabled, UsageState usage, CancellationToken ct)
    {
        await _reconcile.WaitAsync(ct);
        try
        {
            var wasActive = _coordinator.IsActive;
            _coordinator.Reconcile(enabled, usage.InUse, usage.LastActivityAt, usage.Now);
            if (enabled && usage.ShouldPreventSleep && !_coordinator.IsActive && !string.IsNullOrWhiteSpace(inhibitor.LastError))
                logger.LogWarning("Could not prevent system sleep while Agent is in use or idle grace: {PowerError}", inhibitor.LastError);
            else if (wasActive != _coordinator.IsActive)
                logger.LogInformation(_coordinator.IsActive
                    ? usage.InUse
                        ? "System sleep prevention acquired for active Agent work."
                        : "System sleep prevention acquired for the post-activity idle grace period."
                    : "System sleep prevention released.");
        }
        finally
        {
            _reconcile.Release();
        }
    }

    private AgentPowerStatus BuildStatus(bool enabled, UsageState usage)
    {
        var graceMinutes = (int)PowerInhibitionCoordinator.IdleGracePeriod.TotalMinutes;
        var message = !inhibitor.Supported
            ? "System sleep prevention is not supported on this platform yet."
            : !enabled
                ? "Prevent Sleep While In Use is off."
                : _coordinator.IsActive
                    ? usage.InUse
                        ? "In use · Sleep prevented"
                        : $"Recently in use · Sleep prevented for up to {graceMinutes} minutes after activity"
                    : usage.ShouldPreventSleep
                        ? "Agent activity was detected, but the OS sleep-prevention request could not be acquired."
                        : "Enabled; normal sleep behavior is active after the idle grace period.";

        return new AgentPowerStatus(enabled, inhibitor.Supported, usage.InUse, _coordinator.IsActive, message, inhibitor.LastError);
    }

    private static DateTimeOffset? Latest(DateTimeOffset? first, DateTimeOffset? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        return first >= second ? first : second;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _coordinator.Release();
        await base.StopAsync(cancellationToken);
    }
}
