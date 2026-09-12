using MateMCP.Agent.Desktop;

namespace MateMCP.Agent.Tests;

public sealed class ComputerUseSessionManagerTests
{
    [Fact]
    public void Touch_starts_a_visible_session_and_persists_status()
    {
        using var temp = new TempComputerUseDirectory();
        var manager = temp.CreateManager();

        var status = manager.Touch("view", "Main display");

        Assert.True(status.Active);
        Assert.False(status.Blocked);
        Assert.False(string.IsNullOrWhiteSpace(status.SessionId));
        Assert.Equal("view", status.Mode);
        Assert.Equal("Main display", status.Target);
        Assert.NotNull(status.StartedAt);
        Assert.NotNull(status.LastActivityAt);
        Assert.True(File.Exists(Path.Combine(temp.Path, ComputerUseSessionManager.StatusFileName)));
    }

    [Fact]
    public void Consecutive_actions_share_the_current_session()
    {
        using var temp = new TempComputerUseDirectory();
        var manager = temp.CreateManager();
        var first = manager.Touch("view", "window list");
        var second = manager.Touch("input", "left click");

        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(first.StartedAt, second.StartedAt);
        Assert.Equal("input", second.Mode);
        Assert.Equal("left click", second.Target);
    }

    [Fact]
    public void Stop_revokes_and_blocks_future_actions()
    {
        using var temp = new TempComputerUseDirectory();
        var manager = temp.CreateManager();
        manager.Touch("input", "click");

        var stopped = manager.Stop();

        Assert.False(stopped.Active);
        Assert.True(stopped.Blocked);
        Assert.NotNull(stopped.RevokedAt);
        Assert.True(File.Exists(Path.Combine(temp.Path, ComputerUseSessionManager.StopFileName)));
        Assert.Throws<InvalidOperationException>(() => manager.EnsureAvailable());
        Assert.Throws<InvalidOperationException>(() => manager.Touch("view", "screen"));
    }

    [Fact]
    public void External_stop_sentinel_is_observed_before_the_next_action()
    {
        using var temp = new TempComputerUseDirectory();
        var manager = temp.CreateManager();
        manager.Touch("view", "screen");
        File.WriteAllText(Path.Combine(temp.Path, ComputerUseSessionManager.StopFileName), "local stop");

        Assert.Throws<InvalidOperationException>(() => manager.EnsureAvailable());
        var status = manager.GetStatus();
        Assert.True(status.Blocked);
        Assert.False(status.Active);
        Assert.Equal("Stopped from local Companion.", status.RevokeReason);
    }

    [Fact]
    public void Resume_does_not_silently_restore_the_old_session()
    {
        using var temp = new TempComputerUseDirectory();
        var manager = temp.CreateManager();
        var previous = manager.Touch("view", "screen");
        manager.Stop();

        var resumed = manager.Resume();

        Assert.False(resumed.Active);
        Assert.False(resumed.Blocked);
        Assert.Null(resumed.SessionId);
        Assert.False(File.Exists(Path.Combine(temp.Path, ComputerUseSessionManager.StopFileName)));
        manager.EnsureAvailable();
        var next = manager.Touch("view", "screen");
        Assert.NotEqual(previous.SessionId, next.SessionId);
    }

    [Fact]
    public void Long_targets_are_bounded_in_status()
    {
        using var temp = new TempComputerUseDirectory();
        var manager = temp.CreateManager();
        var status = manager.Touch("input", new string('x', 500));
        Assert.NotNull(status.Target);
        Assert.True(status.Target!.Length <= 161);
    }

    private sealed class TempComputerUseDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "matemcp-computer-use-tests", Guid.NewGuid().ToString("n"));

        public TempComputerUseDirectory() => Directory.CreateDirectory(Path);

        public ComputerUseSessionManager CreateManager() => new(Path);

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
