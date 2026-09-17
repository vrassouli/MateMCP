using MateMCP.Agent.Desktop;

namespace MateMCP.Agent.Tests;

public sealed class PowerInhibitionCoordinatorTests
{
    [Fact]
    public void Disabled_preference_never_acquires_inhibitor()
    {
        var inhibitor = new FakePowerInhibitor();
        var coordinator = new PowerInhibitionCoordinator(inhibitor);

        coordinator.Reconcile(enabled: false, inUse: true);

        Assert.False(coordinator.IsActive);
        Assert.Equal(0, inhibitor.AcquireCalls);
        Assert.True(inhibitor.ReleaseCalls >= 1);
    }

    [Fact]
    public void Idle_active_idle_transition_acquires_then_releases()
    {
        var inhibitor = new FakePowerInhibitor();
        var coordinator = new PowerInhibitionCoordinator(inhibitor);

        coordinator.Reconcile(enabled: true, inUse: false);
        Assert.False(coordinator.IsActive);

        coordinator.Reconcile(enabled: true, inUse: true);
        Assert.True(coordinator.IsActive);
        Assert.Equal(1, inhibitor.AcquireCalls);

        coordinator.Reconcile(enabled: true, inUse: false);
        Assert.False(coordinator.IsActive);
        Assert.True(inhibitor.ReleaseCalls >= 2);
    }

    [Fact]
    public void Repeated_active_reconciliation_is_reference_safe()
    {
        var inhibitor = new FakePowerInhibitor();
        var coordinator = new PowerInhibitionCoordinator(inhibitor);

        coordinator.Reconcile(enabled: true, inUse: true);
        coordinator.Reconcile(enabled: true, inUse: true);

        Assert.True(coordinator.IsActive);
        Assert.Equal(1, inhibitor.AcquireCalls);

        coordinator.Release();
        Assert.False(coordinator.IsActive);
    }

    [Fact]
    public void Disabling_while_active_releases_immediately()
    {
        var inhibitor = new FakePowerInhibitor();
        var coordinator = new PowerInhibitionCoordinator(inhibitor);
        coordinator.Reconcile(enabled: true, inUse: true);

        coordinator.Reconcile(enabled: false, inUse: true);

        Assert.False(coordinator.IsActive);
    }


    [Fact]
    public void Recent_activity_keeps_inhibitor_acquired_during_idle_grace()
    {
        var inhibitor = new FakePowerInhibitor();
        var coordinator = new PowerInhibitionCoordinator(inhibitor);
        var now = new DateTimeOffset(2026, 9, 17, 20, 0, 0, TimeSpan.Zero);

        coordinator.Reconcile(
            enabled: true,
            inUse: false,
            lastActivityAt: now - TimeSpan.FromMinutes(10),
            now);

        Assert.True(coordinator.IsActive);
        Assert.Equal(1, inhibitor.AcquireCalls);
    }

    [Fact]
    public void Expired_idle_grace_releases_inhibitor()
    {
        var inhibitor = new FakePowerInhibitor();
        var coordinator = new PowerInhibitionCoordinator(inhibitor);
        var now = new DateTimeOffset(2026, 9, 17, 20, 0, 0, TimeSpan.Zero);
        coordinator.Reconcile(enabled: true, inUse: true);

        coordinator.Reconcile(
            enabled: true,
            inUse: false,
            lastActivityAt: now - PowerInhibitionCoordinator.IdleGracePeriod,
            now);

        Assert.False(coordinator.IsActive);
    }

    [Fact]
    public void Future_or_missing_activity_does_not_start_idle_grace()
    {
        var now = new DateTimeOffset(2026, 9, 17, 20, 0, 0, TimeSpan.Zero);

        Assert.False(PowerInhibitionCoordinator.IsWithinIdleGrace(null, now));
        Assert.False(PowerInhibitionCoordinator.IsWithinIdleGrace(now + TimeSpan.FromSeconds(1), now));
    }

    [Fact]
    public void Failed_acquire_does_not_report_sleep_prevention_active()
    {
        var inhibitor = new FakePowerInhibitor { AcquireSucceeds = false };
        var coordinator = new PowerInhibitionCoordinator(inhibitor);

        coordinator.Reconcile(enabled: true, inUse: true);

        Assert.False(coordinator.IsActive);
        Assert.Equal(1, inhibitor.AcquireCalls);
    }

    private sealed class FakePowerInhibitor : IPowerInhibitor
    {
        public bool Supported => true;
        public bool IsActive { get; private set; }
        public string? LastError => AcquireSucceeds ? null : "test failure";
        public bool AcquireSucceeds { get; init; } = true;
        public int AcquireCalls { get; private set; }
        public int ReleaseCalls { get; private set; }

        public bool Acquire()
        {
            if (IsActive) return true;
            AcquireCalls++;
            IsActive = AcquireSucceeds;
            return IsActive;
        }

        public void Release()
        {
            ReleaseCalls++;
            IsActive = false;
        }

        public void Dispose() => Release();
    }
}
