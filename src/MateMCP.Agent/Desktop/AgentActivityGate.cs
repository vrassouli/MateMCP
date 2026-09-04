namespace MateMCP.Agent.Desktop;

/// <summary>
/// Tracks active Agent work and prevents a Desktop update handoff from racing
/// with running operations. Normal operations take a lease; the updater can
/// begin a drain only when no leased work is active. Once draining starts,
/// new leased work is rejected.
/// </summary>
public sealed class AgentActivityGate
{
    private int _active;
    private int _draining;
    private long _activeSinceUnixMilliseconds;

    public int ActiveCount => Volatile.Read(ref _active);
    public bool IsActive => ActiveCount > 0;
    public bool IsDraining => Volatile.Read(ref _draining) != 0;

    public DateTimeOffset? ActiveSince
    {
        get
        {
            var value = Volatile.Read(ref _activeSinceUnixMilliseconds);
            return value <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
    }

    public bool TryEnter(out IDisposable? lease)
    {
        lease = null;
        if (IsDraining) return false;

        var active = Interlocked.Increment(ref _active);
        if (active == 1)
            Interlocked.Exchange(ref _activeSinceUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        if (IsDraining)
        {
            Exit();
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

    private void Exit()
    {
        if (Interlocked.Decrement(ref _active) == 0)
            Interlocked.Exchange(ref _activeSinceUnixMilliseconds, 0);
    }

    private sealed class ActivityLease(AgentActivityGate owner) : IDisposable
    {
        private AgentActivityGate? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            current?.Exit();
        }
    }
}
