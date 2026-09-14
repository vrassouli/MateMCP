using System.Collections.Concurrent;

namespace MateMCP.Agent.Tools;

internal sealed class ShellReplayCheckpointRegistry
{
    private const int MaxEntries = 256;
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, CheckpointState> _states = new(StringComparer.Ordinal);

    public ShellReplaySnapshot Read(
        ShellSessionSnapshot fullSnapshot,
        int requestedSequence,
        int? acknowledgedSequence = null)
    {
        if (requestedSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(requestedSequence), "Shell output sequence cannot be negative.");

        Prune();
        var state = _states.GetOrAdd(fullSnapshot.SessionId, static _ => new CheckpointState());
        var nextSequence = fullSnapshot.NextOffset;
        var firstAvailableSequence = Math.Max(0, nextSequence - fullSnapshot.Output.Length);

        int acknowledged;
        lock (state.Gate)
        {
            if (acknowledgedSequence is not null)
            {
                if (acknowledgedSequence.Value < state.AcknowledgedSequence)
                    throw new ArgumentOutOfRangeException(nameof(acknowledgedSequence),
                        $"Acknowledged sequence cannot move backwards from {state.AcknowledgedSequence} to {acknowledgedSequence.Value}.");
                if (acknowledgedSequence.Value > nextSequence)
                    throw new ArgumentOutOfRangeException(nameof(acknowledgedSequence),
                        $"Acknowledged sequence {acknowledgedSequence.Value} exceeds the produced sequence {nextSequence}.");
                state.AcknowledgedSequence = acknowledgedSequence.Value;
            }

            state.LastTouched = DateTimeOffset.UtcNow;
            acknowledged = state.AcknowledgedSequence;
        }

        var replayGap = requestedSequence < firstAvailableSequence;
        var effectiveSequence = Math.Clamp(requestedSequence, firstAvailableSequence, nextSequence);
        var outputStart = effectiveSequence - firstAvailableSequence;
        var output = outputStart >= fullSnapshot.Output.Length
            ? string.Empty
            : fullSnapshot.Output[outputStart..];

        return new ShellReplaySnapshot(
            fullSnapshot.SessionId,
            fullSnapshot.ProcessId,
            output,
            nextSequence,
            fullSnapshot.OutputTruncated,
            fullSnapshot.Exited,
            fullSnapshot.ExitCode,
            fullSnapshot.WorkingDirectory,
            fullSnapshot.CreatedAt,
            fullSnapshot.LastTouched,
            requestedSequence,
            firstAvailableSequence,
            nextSequence,
            acknowledged,
            replayGap);
    }

    public void Remove(string sessionId)
        => _states.TryRemove(sessionId, out _);

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        foreach (var pair in _states)
        {
            if (pair.Value.LastTouched >= cutoff) continue;
            _states.TryRemove(pair.Key, out _);
        }

        if (_states.Count <= MaxEntries) return;
        foreach (var id in _states
                     .OrderBy(pair => pair.Value.LastTouched)
                     .Take(_states.Count - MaxEntries)
                     .Select(pair => pair.Key)
                     .ToArray())
            _states.TryRemove(id, out _);
    }

    private sealed class CheckpointState
    {
        public object Gate { get; } = new();
        public int AcknowledgedSequence { get; set; }
        public DateTimeOffset LastTouched { get; set; } = DateTimeOffset.UtcNow;
    }
}

public sealed record ShellReplaySnapshot(
    string SessionId,
    int ProcessId,
    string Output,
    int NextOffset,
    bool OutputTruncated,
    bool Exited,
    int? ExitCode,
    string WorkingDirectory,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastTouched,
    int RequestedSequence,
    int FirstAvailableSequence,
    int NextSequence,
    int AcknowledgedSequence,
    bool ReplayGap);
