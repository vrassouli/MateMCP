using MateMCP.Agent.Tools;

namespace MateMCP.Agent.Tests;

public sealed class AgentFileTransferResumeTests
{
    [Fact]
    public async Task Exact_committed_chunk_replay_is_idempotent_after_lost_response()
    {
        var root = CreateTempRoot();
        try
        {
            using var manager = new AgentFileTransferManager();
            var bytes = new byte[] { 1, 2, 3, 4, 5, 6 };
            var started = manager.Start(root, "resume.bin", null, bytes.Length, null, null);
            var firstChunk = bytes[..3];

            var first = await manager.AppendChunkAsync(started.TransferId, 0, Convert.ToBase64String(firstChunk));
            var replay = await manager.AppendChunkAsync(started.TransferId, 0, Convert.ToBase64String(firstChunk));

            Assert.False(first.Replayed);
            Assert.True(replay.Replayed);
            Assert.Equal(3, replay.BytesReceived);

            await manager.AppendChunkAsync(started.TransferId, 3, Convert.ToBase64String(bytes[3..]));
            var completed = await manager.CompleteAsync(started.TransferId);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(completed.RemotePath));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Conflicting_replay_is_rejected_without_changing_checkpoint()
    {
        var root = CreateTempRoot();
        try
        {
            using var manager = new AgentFileTransferManager();
            var started = manager.Start(root, "conflict.bin", null, 6, null, null);
            await manager.AppendChunkAsync(started.TransferId, 0, Convert.ToBase64String(new byte[] { 1, 2, 3 }));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.AppendChunkAsync(started.TransferId, 0, Convert.ToBase64String(new byte[] { 1, 9, 3 })));
            var status = await manager.GetStatusAsync(started.TransferId);

            Assert.Contains("conflicts", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(3, status.CommittedOffset);
            Assert.False(status.Completed);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Status_returns_committed_resume_offset_and_gap_is_explicit()
    {
        var root = CreateTempRoot();
        try
        {
            using var manager = new AgentFileTransferManager();
            var started = manager.Start(root, "status.bin", null, 8, null, null);
            await manager.AppendChunkAsync(started.TransferId, 0, Convert.ToBase64String(new byte[] { 1, 2, 3 }));

            var status = await manager.GetStatusAsync(started.TransferId);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.AppendChunkAsync(started.TransferId, 5, Convert.ToBase64String(new byte[] { 4 })));

            Assert.Equal(3, status.CommittedOffset);
            Assert.Equal(8, status.ExpectedSize);
            Assert.False(status.ReadyToComplete);
            Assert.Contains("Expected next offset 3", ex.Message, StringComparison.Ordinal);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Replay_cannot_cross_the_committed_boundary()
    {
        var root = CreateTempRoot();
        try
        {
            using var manager = new AgentFileTransferManager();
            var started = manager.Start(root, "overlap.bin", null, 8, null, null);
            await manager.AppendChunkAsync(started.TransferId, 0, Convert.ToBase64String(new byte[] { 1, 2, 3 }));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.AppendChunkAsync(started.TransferId, 2, Convert.ToBase64String(new byte[] { 3, 4 })));

            Assert.Contains("uncommitted boundary", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(3, (await manager.GetStatusAsync(started.TransferId)).CommittedOffset);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task Completion_is_repeatable_after_lost_response()
    {
        var root = CreateTempRoot();
        try
        {
            using var manager = new AgentFileTransferManager();
            var bytes = new byte[] { 7, 8, 9 };
            var started = manager.Start(root, "done.bin", null, bytes.Length, null, null);
            await manager.AppendChunkAsync(started.TransferId, 0, Convert.ToBase64String(bytes));

            var first = await manager.CompleteAsync(started.TransferId);
            var second = await manager.CompleteAsync(started.TransferId);
            var status = await manager.GetStatusAsync(started.TransferId);

            Assert.Equal(first, second);
            Assert.True(status.Completed);
            Assert.True(status.ReadyToComplete);
            Assert.Equal(bytes.Length, status.CommittedOffset);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(second.RemotePath));
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
