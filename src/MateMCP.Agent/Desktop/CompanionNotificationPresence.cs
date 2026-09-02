namespace MateMCP.Agent.Desktop;

/// <summary>
/// Tracks a short-lived heartbeat from a Companion instance whose native approval
/// notifier has completed initialization and is available. A heartbeat deliberately
/// expires quickly so Agent fallback notifications recover after Companion startup
/// failures, crashes, permission loss, or Agent restarts.
/// </summary>
public sealed class CompanionNotificationPresence
{
    public static readonly TimeSpan HeartbeatLifetime = TimeSpan.FromSeconds(6);
    private long _lastReadyUtcTicks;

    public void MarkReady() => MarkReady(DateTimeOffset.UtcNow);

    public void MarkReady(DateTimeOffset now)
        => Interlocked.Exchange(ref _lastReadyUtcTicks, now.UtcTicks);

    public bool IsReady => IsReadyAt(DateTimeOffset.UtcNow);

    public bool IsReadyAt(DateTimeOffset now)
    {
        var value = Volatile.Read(ref _lastReadyUtcTicks);
        if (value <= 0) return false;

        var lastReady = new DateTimeOffset(value, TimeSpan.Zero);
        var age = now - lastReady;
        return age >= TimeSpan.Zero && age <= HeartbeatLifetime;
    }
}
