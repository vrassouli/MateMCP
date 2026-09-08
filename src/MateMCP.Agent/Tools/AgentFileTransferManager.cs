using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MateMCP.Agent.Tools;

public sealed class AgentFileTransferManager : IDisposable
{
    public const int MaxChunkBytes = 512 * 1024;
    private static readonly TimeSpan IncompleteTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan CompletedTtl = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, TransferState> _transfers = new(StringComparer.Ordinal);
    private readonly Timer _cleanupTimer;

    public AgentFileTransferManager()
    {
        _cleanupTimer = new Timer(_ => _ = CleanupTimerTickAsync(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public TransferStarted Start(string root, string fileName, string? mimeType, long size, string? sha256, string? project)
    {
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size), "File size cannot be negative.");
        if (size > 50L * 1024 * 1024 * 1024) throw new InvalidOperationException("Files larger than 50 GiB are not accepted.");

        fileName = SanitizeFileName(fileName);
        sha256 = NormalizeSha256(sha256);
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetFullPath(root), id);
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, fileName);
        var partialPath = finalPath + ".part";

        using (File.Create(partialPath)) { }
        var state = new TransferState(id, project, fileName, mimeType?.Trim(), size, sha256, partialPath, finalPath);
        if (!_transfers.TryAdd(id, state)) throw new InvalidOperationException("Could not allocate transfer id.");

        return new TransferStarted(id, project, fileName, mimeType, size, sha256, finalPath, MaxChunkBytes, IncompleteTtl);
    }

    public async Task<TransferProgress> AppendChunkAsync(string transferId, long offset, string base64Data, CancellationToken cancellationToken = default)
    {
        var state = Get(transferId);
        byte[] chunk;
        try { chunk = Convert.FromBase64String(base64Data); }
        catch (FormatException ex) { throw new InvalidOperationException("Chunk data is not valid base64.", ex); }
        if (chunk.Length == 0) throw new InvalidOperationException("Chunk is empty.");
        if (chunk.Length > MaxChunkBytes) throw new InvalidOperationException($"Chunk exceeds the {MaxChunkBytes}-byte limit.");

        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            EnsureWritable(state);
            if (offset != state.BytesWritten) throw new InvalidOperationException($"Unexpected chunk offset. Expected {state.BytesWritten}, received {offset}.");
            if (state.BytesWritten + chunk.LongLength > state.ExpectedSize) throw new InvalidOperationException("Chunk would exceed the declared file size.");

            await using var stream = new FileStream(state.PartialPath, FileMode.Open, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.Position = state.BytesWritten;
            await stream.WriteAsync(chunk.AsMemory(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
            state.BytesWritten += chunk.LongLength;
            state.LastTouchedUtc = DateTimeOffset.UtcNow;
            return new TransferProgress(state.Id, state.BytesWritten, state.ExpectedSize, state.BytesWritten == state.ExpectedSize);
        }
        finally { state.Gate.Release(); }
    }

    public async Task<TransferCompleted> CompleteAsync(string transferId, CancellationToken cancellationToken = default)
    {
        var state = Get(transferId);
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            EnsureWritable(state);
            if (state.BytesWritten != state.ExpectedSize) throw new InvalidOperationException($"Transfer is incomplete: received {state.BytesWritten} of {state.ExpectedSize} bytes.");

            string actualSha256;
            await using (var stream = new FileStream(state.PartialPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var hash = await SHA256.HashDataAsync(stream, cancellationToken);
                actualSha256 = Convert.ToHexString(hash).ToLowerInvariant();
            }
            if (state.ExpectedSha256 is not null && !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(state.ExpectedSha256), Convert.FromHexString(actualSha256)))
                throw new InvalidOperationException("SHA-256 integrity check failed.");

            File.Move(state.PartialPath, state.FinalPath, overwrite: false);
            state.Completed = true;
            state.LastTouchedUtc = DateTimeOffset.UtcNow;
            return new TransferCompleted(state.Id, state.Project, state.FileName, state.MimeType, state.ExpectedSize, actualSha256, state.FinalPath, CompletedTtl);
        }
        finally { state.Gate.Release(); }
    }

    public async Task CancelAsync(string transferId, CancellationToken cancellationToken = default)
    {
        if (!_transfers.TryRemove(transferId, out var state)) return;
        await state.Gate.WaitAsync(cancellationToken);
        try { DeleteTransferFiles(state); }
        finally { state.Gate.Release(); state.Gate.Dispose(); }
    }

    internal async Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var removed = 0;
        foreach (var pair in _transfers)
        {
            var ttl = pair.Value.Completed ? CompletedTtl : IncompleteTtl;
            if (now - pair.Value.LastTouchedUtc < ttl) continue;
            if (!_transfers.TryRemove(pair.Key, out var state)) continue;
            await state.Gate.WaitAsync(cancellationToken);
            try { DeleteTransferFiles(state); removed++; }
            finally { state.Gate.Release(); state.Gate.Dispose(); }
        }
        return removed;
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        foreach (var pair in _transfers.ToArray())
        {
            if (!_transfers.TryRemove(pair.Key, out var state)) continue;
            state.Gate.Wait();
            try { DeleteTransferFiles(state); }
            finally { state.Gate.Release(); state.Gate.Dispose(); }
        }
    }

    private async Task CleanupTimerTickAsync()
    {
        try { await CleanupExpiredAsync(); }
        catch { }
    }

    private TransferState Get(string transferId)
    {
        if (string.IsNullOrWhiteSpace(transferId) || !_transfers.TryGetValue(transferId, out var state))
            throw new KeyNotFoundException("Transfer was not found or has expired.");
        return state;
    }

    private static void EnsureWritable(TransferState state)
    {
        if (state.Completed) throw new InvalidOperationException("Transfer is already complete.");
        if (!File.Exists(state.PartialPath)) throw new IOException("Transfer temporary file is missing.");
    }

    private static void DeleteTransferFiles(TransferState state)
    {
        try { if (File.Exists(state.PartialPath)) File.Delete(state.PartialPath); } catch { }
        try { if (File.Exists(state.FinalPath)) File.Delete(state.FinalPath); } catch { }
        try
        {
            var directory = Path.GetDirectoryName(state.FinalPath);
            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch { }
    }

    private static string SanitizeFileName(string fileName)
    {
        fileName = Path.GetFileName(fileName.Trim()) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..") fileName = "attachment.bin";
        foreach (var invalid in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(invalid, '_');
        if (fileName.Length > 180)
        {
            var extension = Path.GetExtension(fileName);
            var stemLength = Math.Max(1, 180 - extension.Length);
            fileName = fileName[..Math.Min(stemLength, fileName.Length)] + extension;
        }
        return fileName;
    }

    private static string? NormalizeSha256(string? sha256)
    {
        if (string.IsNullOrWhiteSpace(sha256)) return null;
        sha256 = sha256.Trim().ToLowerInvariant();
        if (sha256.Length != 64 || sha256.Any(c => !Uri.IsHexDigit(c))) throw new InvalidOperationException("sha256 must be a 64-character hexadecimal SHA-256 digest.");
        return sha256;
    }

    private sealed class TransferState(string id, string? project, string fileName, string? mimeType, long expectedSize, string? expectedSha256, string partialPath, string finalPath)
    {
        public string Id { get; } = id;
        public string? Project { get; } = project;
        public string FileName { get; } = fileName;
        public string? MimeType { get; } = mimeType;
        public long ExpectedSize { get; } = expectedSize;
        public string? ExpectedSha256 { get; } = expectedSha256;
        public string PartialPath { get; } = partialPath;
        public string FinalPath { get; } = finalPath;
        public long BytesWritten { get; set; }
        public bool Completed { get; set; }
        public DateTimeOffset LastTouchedUtc { get; set; } = DateTimeOffset.UtcNow;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }
}

public sealed record TransferStarted(string TransferId, string? Project, string FileName, string? MimeType, long Size, string? ExpectedSha256, string RemotePath, int MaxChunkBytes, TimeSpan IncompleteTtl);
public sealed record TransferProgress(string TransferId, long BytesReceived, long ExpectedSize, bool ReadyToComplete);
public sealed record TransferCompleted(string TransferId, string? Project, string FileName, string? MimeType, long Size, string Sha256, string RemotePath, TimeSpan CleanupAfter);
