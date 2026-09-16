namespace MateMCP.Agent.Tools;

/// <summary>
/// Stable wire/result contract for shell_session_start. Keeps the existing shell
/// snapshot fields at the top level and adds optional proactive/context-preflight metadata.
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
    string? MemoryContext,
    string Status = "started",
    string? ContextLease = null,
    string? ContextHash = null,
    IReadOnlyList<string>? ContextSources = null,
    string? ContextId = null,
    string? Context = null,
    bool Started = true)
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

    public static ShellSessionStartResult ContextRequired(
        string workingDirectory,
        string contextLease,
        string contextHash,
        IReadOnlyList<string> contextSources,
        string? contextId,
        string context) => new(
            SessionId: string.Empty,
            ProcessId: 0,
            Output: string.Empty,
            NextOffset: 0,
            OutputTruncated: false,
            Exited: false,
            ExitCode: null,
            WorkingDirectory: workingDirectory,
            CreatedAt: default,
            LastTouched: default,
            RequestedSequence: 0,
            FirstAvailableSequence: 0,
            NextSequence: 0,
            AcknowledgedSequence: 0,
            ReplayGap: false,
            MemoryContext: null,
            Status: "context_required",
            ContextLease: contextLease,
            ContextHash: contextHash,
            ContextSources: contextSources,
            ContextId: contextId,
            Context: context,
            Started: false);
}
