using MateMCP.Agent.Desktop;

namespace MateMCP.Agent.Tests;

public sealed class AgentActivityGateTests
{
    [Fact]
    public void Active_work_prevents_update_drain()
    {
        var gate = new AgentActivityGate();
        Assert.True(gate.TryEnter(out var lease));
        Assert.NotNull(lease);
        Assert.True(gate.IsActive);
        Assert.Equal(1, gate.ActiveCount);
        Assert.NotNull(gate.ActiveSince);

        Assert.False(gate.TryBeginDrain());
        Assert.False(gate.IsDraining);

        lease!.Dispose();
        Assert.False(gate.IsActive);
        Assert.Equal(0, gate.ActiveCount);
        Assert.Null(gate.ActiveSince);
        Assert.True(gate.TryBeginDrain());
        Assert.True(gate.IsDraining);
    }

    [Fact]
    public void Concurrent_work_keeps_activity_active_until_last_lease_finishes()
    {
        var gate = new AgentActivityGate();
        Assert.True(gate.TryEnter(out var first));
        Assert.True(gate.TryEnter(out var second));
        Assert.Equal(2, gate.ActiveCount);

        first!.Dispose();
        Assert.True(gate.IsActive);
        Assert.Equal(1, gate.ActiveCount);
        Assert.NotNull(gate.ActiveSince);

        second!.Dispose();
        Assert.False(gate.IsActive);
        Assert.Equal(0, gate.ActiveCount);
        Assert.Null(gate.ActiveSince);
    }

    [Fact]
    public void Activity_lease_cleanup_is_idempotent_after_failure_or_cancellation_paths()
    {
        var gate = new AgentActivityGate();
        Assert.True(gate.TryEnter(out var lease));

        lease!.Dispose();
        lease.Dispose();

        Assert.False(gate.IsActive);
        Assert.Equal(0, gate.ActiveCount);
        Assert.Null(gate.ActiveSince);
    }

    [Fact]
    public void Update_drain_rejects_new_long_running_work_until_cancelled()
    {
        var gate = new AgentActivityGate();
        Assert.True(gate.TryBeginDrain());
        Assert.False(gate.TryEnter(out _));

        gate.CancelDrain();
        Assert.True(gate.TryEnter(out var lease));
        lease!.Dispose();
    }
}
