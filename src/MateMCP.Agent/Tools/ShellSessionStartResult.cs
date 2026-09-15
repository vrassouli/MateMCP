namespace MateMCP.Agent.Tools;

/// <summary>
/// Stable wire/result contract for shell_session_start. Keeps the existing shell
/// snapshot fields at the top level and adds optional proactive memory context.
/// </summary>
public sealed record ShellSessionStartResult(
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
    bool ReplayGap,
    string? MemoryContext)
{
    public static ShellSessionStartResult FromSnapshot(ShellSessionSnapshot snapshot, string? memoryContext) => new(
        snapshot.SessionId,
        snapshot.ProcessId,
        snapshot.Output,
        snapshot.NextOffset,
        snapshot.OutputTruncated,
        snapshot.Exited,
        snapshot.ExitCode,
        snapshot.WorkingDirectory,
        snapshot.CreatedAt,
        snapshot.LastTouched,
        snapshot.RequestedSequence,
        snapshot.FirstAvailableSequence,
        snapshot.NextSequence,
        snapshot.AcknowledgedSequence,
        snapshot.ReplayGap,
        memoryContext);
}
