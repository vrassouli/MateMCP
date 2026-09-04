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
    public bool IsActive => inhibitor.IsActive;

    public void Reconcile(bool enabled, bool inUse)
    {
        if (enabled && inUse)
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

    public void RequestReconcile()
    {
        if (_wake.CurrentCount == 0)
            _wake.Release();
    }

    public async Task<AgentPowerStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var current = await settings.GetAsync(ct);
        var inUse = activity.IsActive || sessions.ActiveSessionCount > 0;
        await ReconcileAsync(current.PreventSleepWhileInUse, inUse, ct);
        return BuildStatus(current.PreventSleepWhileInUse, inUse);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var current = await settings.GetAsync(stoppingToken);
                var inUse = activity.IsActive || sessions.ActiveSessionCount > 0;
                await ReconcileAsync(current.PreventSleepWhileInUse, inUse, stoppingToken);
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

    private async Task ReconcileAsync(bool enabled, bool inUse, CancellationToken ct)
    {
        await _reconcile.WaitAsync(ct);
        try
        {
            var wasActive = _coordinator.IsActive;
            _coordinator.Reconcile(enabled, inUse);
            if (enabled && inUse && !_coordinator.IsActive && !string.IsNullOrWhiteSpace(inhibitor.LastError))
                logger.LogWarning("Could not prevent system sleep while Agent is in use: {PowerError}", inhibitor.LastError);
            else if (wasActive != _coordinator.IsActive)
                logger.LogInformation(_coordinator.IsActive ? "System sleep prevention acquired for active Agent work." : "System sleep prevention released.");
        }
        finally
        {
            _reconcile.Release();
        }
    }

    private AgentPowerStatus BuildStatus(bool enabled, bool inUse)
    {
        var message = !inhibitor.Supported
            ? "System sleep prevention is not supported on this platform yet."
            : !enabled
                ? "Prevent Sleep While In Use is off."
                : _coordinator.IsActive
                    ? "In use · Sleep prevented"
                    : inUse
                        ? "Agent is in use, but the OS sleep-prevention request could not be acquired."
                        : "Enabled; normal sleep behavior is active while Agent is idle.";

        return new AgentPowerStatus(enabled, inhibitor.Supported, inUse, _coordinator.IsActive, message, inhibitor.LastError);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _coordinator.Release();
        await base.StopAsync(cancellationToken);
    }
}
