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
        Assert.Equal(1, gate.ActiveCount);

        Assert.False(gate.TryBeginDrain());
        Assert.False(gate.IsDraining);

        lease!.Dispose();
        Assert.Equal(0, gate.ActiveCount);
        Assert.True(gate.TryBeginDrain());
        Assert.True(gate.IsDraining);
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
