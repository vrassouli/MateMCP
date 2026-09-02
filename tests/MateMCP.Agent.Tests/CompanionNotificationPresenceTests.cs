using MateMCP.Agent.Desktop;

namespace MateMCP.Agent.Tests;

public sealed class CompanionNotificationPresenceTests
{
    [Fact]
    public void Presence_is_not_ready_before_first_heartbeat()
    {
        var presence = new CompanionNotificationPresence();

        Assert.False(presence.IsReadyAt(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Fresh_heartbeat_marks_companion_notifier_ready()
    {
        var now = DateTimeOffset.UtcNow;
        var presence = new CompanionNotificationPresence();

        presence.MarkReady(now);

        Assert.True(presence.IsReadyAt(now));
        Assert.True(presence.IsReadyAt(now.Add(CompanionNotificationPresence.HeartbeatLifetime)));
    }

    [Fact]
    public void Stale_heartbeat_expires_and_restores_agent_fallback()
    {
        var now = DateTimeOffset.UtcNow;
        var presence = new CompanionNotificationPresence();
        presence.MarkReady(now);

        Assert.False(presence.IsReadyAt(now.Add(CompanionNotificationPresence.HeartbeatLifetime).AddMilliseconds(1)));
    }
}
