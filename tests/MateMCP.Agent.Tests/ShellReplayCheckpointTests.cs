using MateMCP.Agent.Tools;

namespace MateMCP.Agent.Tests;

public sealed class ShellReplayCheckpointTests
{
    [Fact]
    public void Read_returns_requested_tail_and_sequence_bounds()
    {
        var registry = new ShellReplayCheckpointRegistry();
        var replay = registry.Read(Snapshot("abcdefghij", 10, false), 4);

        Assert.Equal("efghij", replay.Output);
        Assert.Equal(0, replay.FirstAvailableSequence);
        Assert.Equal(10, replay.NextSequence);
        Assert.Equal(10, replay.NextOffset);
        Assert.False(replay.ReplayGap);
    }

    [Fact]
    public void Acknowledgement_survives_later_reads()
    {
        var registry = new ShellReplayCheckpointRegistry();
        _ = registry.Read(Snapshot("abcdefghij", 10, false), 0, 6);
        var later = registry.Read(Snapshot("abcdefghijkl", 12, false), 6);

        Assert.Equal(6, later.AcknowledgedSequence);
        Assert.Equal("ghijkl", later.Output);
    }

    [Fact]
    public void Invalid_acknowledgements_are_rejected()
    {
        var registry = new ShellReplayCheckpointRegistry();
        var snapshot = Snapshot("abcdefghij", 10, false);
        _ = registry.Read(snapshot, 0, 7);

        Assert.Throws<ArgumentOutOfRangeException>(() => registry.Read(snapshot, 7, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.Read(snapshot, 7, 11));
    }

    [Fact]
    public void Old_sequence_reports_replay_gap_after_buffer_trim()
    {
        var registry = new ShellReplayCheckpointRegistry();
        var replay = registry.Read(Snapshot(new string('x', 20), 100, true), 50);

        Assert.True(replay.ReplayGap);
        Assert.Equal(80, replay.FirstAvailableSequence);
        Assert.Equal(100, replay.NextSequence);
        Assert.Equal(20, replay.Output.Length);
    }

    [Fact]
    public void Available_checkpoint_replays_without_gap()
    {
        var registry = new ShellReplayCheckpointRegistry();
        var replay = registry.Read(Snapshot("uvwxyz", 100, true), 96, 96);

        Assert.False(replay.ReplayGap);
        Assert.Equal(94, replay.FirstAvailableSequence);
        Assert.Equal("yz", replay.Output);
        Assert.Equal(96, replay.AcknowledgedSequence);
    }

    [Fact]
    public void Removing_session_resets_ack_state()
    {
        var registry = new ShellReplayCheckpointRegistry();
        var snapshot = Snapshot("abc", 3, false);
        _ = registry.Read(snapshot, 0, 2);
        registry.Remove(snapshot.SessionId);

        Assert.Equal(0, registry.Read(snapshot, 0).AcknowledgedSequence);
    }

    private static ShellSessionSnapshot Snapshot(string output, int nextOffset, bool truncated)
        => new("shell-session", 123, output, nextOffset, truncated, false, null,
            Environment.CurrentDirectory, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
}
