namespace MateMCP.Agent.Diagnostics;

/// <summary>
/// Prevents multiple Agent processes for the same user profile from competing for
/// the local management endpoint. The operating system releases the exclusive
/// file handle automatically when the owning process exits or crashes.
/// </summary>
internal sealed class AgentSingleInstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private AgentSingleInstanceLock(FileStream stream)
    {
        _stream = stream;
    }

    public static AgentSingleInstanceLock? TryAcquire(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "agent.lock");

        try
        {
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);

            stream.SetLength(0);
            using (var writer = new StreamWriter(stream, leaveOpen: true))
            {
                writer.Write($"pid={Environment.ProcessId};started={DateTimeOffset.UtcNow:O}");
                writer.Flush();
            }

            stream.Position = 0;
            return new AgentSingleInstanceLock(stream);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
