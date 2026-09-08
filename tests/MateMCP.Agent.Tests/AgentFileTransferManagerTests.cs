using System.Security.Cryptography;
using MateMCP.Agent.Tools;

namespace MateMCP.Agent.Tests;

public sealed class AgentFileTransferManagerTests
{
    [Fact]
    public async Task StreamsChunksToDiskAndVerifiesIntegrity()
    {
        var root = CreateTempRoot();
        try
        {
            using var manager = new AgentFileTransferManager();
            var bytes = Enumerable.Range(0, AgentFileTransferManager.MaxChunkBytes + 37).Select(i => (byte)(i % 251)).ToArray();
            var expectedSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var started = manager.Start(root, "sample.bin", "application/octet-stream", bytes.LongLength, expectedSha, "demo");

            var first = bytes[..AgentFileTransferManager.MaxChunkBytes];
            var second = bytes[AgentFileTransferManager.MaxChunkBytes..];
            var progress1 = await manager.AppendChunkAsync(started.TransferId, 0, Convert.ToBase64String(first));
            var progress2 = await manager.AppendChunkAsync(started.TransferId, progress1.BytesReceived, Convert.ToBase64String(second));
            var completed = await manager.CompleteAsync(started.TransferId);

            Assert.False(progress1.ReadyToComplete);
            Assert.True(progress2.ReadyToComplete);
            Assert.Equal(bytes.LongLength, completed.Size);
            Assert.Equal(expectedSha, completed.Sha256);
            Assert.Equal("sample.bin", completed.FileName);
            Assert.Equal("application/octet-stream", completed.MimeType);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(completed.RemotePath));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task RejectsOutOfOrderChunkWithoutCorruptingTransfer()
    {
        var root = CreateTempRoot();
        try
        {
            using var manager = new AgentFileTransferManager();
            var bytes = new byte[] { 1, 2, 3, 4 };
            var started = manager.Start(root, "x.bin", null, bytes.Length, null, null);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.AppendChunkAsync(started.TransferId, 2, Convert.ToBase64String(bytes)));
            Assert.Contains("Expected 0", ex.Message);

            await manager.AppendChunkAsync(started.TransferId, 0, Convert.ToBase64String(bytes));
            var completed = await manager.CompleteAsync(started.TransferId);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(completed.RemotePath));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task RejectsIntegrityMismatchAndAllowsCleanup()
    {
        var root = CreateTempRoot();
        try
        {
            using var manager = new AgentFileTransferManager();
            var bytes = new byte[] { 9, 8, 7 };
            var wrongSha = new string('0', 64);
            var started = manager.Start(root, "mismatch.bin", null, bytes.Length, wrongSha, null);
            await manager.AppendChunkAsync(started.TransferId, 0, Convert.ToBase64String(bytes));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.CompleteAsync(started.TransferId));
            Assert.Contains("integrity", ex.Message, StringComparison.OrdinalIgnoreCase);

            await manager.CancelAsync(started.TransferId);
            Assert.False(File.Exists(started.RemotePath));
            Assert.False(File.Exists(started.RemotePath + ".part"));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task CancelRemovesInterruptedTransferAndInvalidatesHandle()
    {
        var root = CreateTempRoot();
        try
        {
            using var manager = new AgentFileTransferManager();
            var started = manager.Start(root, "interrupted.bin", null, 10, null, null);
            await manager.AppendChunkAsync(started.TransferId, 0, Convert.ToBase64String(new byte[] { 1, 2, 3 }));

            await manager.CancelAsync(started.TransferId);

            Assert.False(File.Exists(started.RemotePath + ".part"));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.AppendChunkAsync(started.TransferId, 3, Convert.ToBase64String(new byte[] { 4 })));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void RejectsOversizedDeclaredTransferBeforeCreatingFile()
    {
        var root = CreateTempRoot();
        try
        {
            using var manager = new AgentFileTransferManager();
            Assert.Throws<InvalidOperationException>(() => manager.Start(root, "huge.bin", null, 50L * 1024 * 1024 * 1024 + 1, null, null));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { TryDelete(root); }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "MateMCP.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
