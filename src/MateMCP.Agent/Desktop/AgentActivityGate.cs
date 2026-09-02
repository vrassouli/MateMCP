namespace MateMCP.Agent.Desktop;

/// <summary>
/// Prevents a Desktop update handoff from racing with long-running Agent work.
/// Normal operations take a lease; the updater can begin a drain only when no
/// leased work is active. Once draining starts, new leased work is rejected.
/// </summary>
public sealed class AgentActivityGate
{
    private int _active;
    private int _draining;

    public int ActiveCount => Volatile.Read(ref _active);
    public bool IsDraining => Volatile.Read(ref _draining) != 0;

    public bool TryEnter(out IDisposable? lease)
    {
        lease = null;
        if (IsDraining) return false;

        Interlocked.Increment(ref _active);
        if (IsDraining)
        {
            Interlocked.Decrement(ref _active);
            return false;
        }

        lease = new ActivityLease(this);
        return true;
    }

    public bool TryBeginDrain()
    {
        if (Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
            return false;

        if (ActiveCount == 0)
            return true;

        Volatile.Write(ref _draining, 0);
        return false;
    }

    public void CancelDrain() => Volatile.Write(ref _draining, 0);

    private sealed class ActivityLease(AgentActivityGate owner) : IDisposable
    {
        private AgentActivityGate? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null)
                Interlocked.Decrement(ref current._active);
        }
    }
}
