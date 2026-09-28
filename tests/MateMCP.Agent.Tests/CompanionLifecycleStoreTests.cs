using MateMCP.Agent.Companion.Services;

namespace MateMCP.Agent.Tests;

public sealed class CompanionLifecycleStoreTests
{
    [Fact]
    public void BeginSession_DetectsPreviousSessionWithoutTerminalEvent()
    {
        var path = CreatePath();

        try
        {
            var first = new CompanionLifecycleStore(path);
            first.BeginSession();
            first.Record("running");

            var second = new CompanionLifecycleStore(path, isProcessAlive: _ => false);
            second.BeginSession();

            Assert.NotNull(second.PreviousUnexpectedExit);
            Assert.Equal("running", second.PreviousUnexpectedExit!.LastEvent);
            Assert.Contains(second.ReadRecent(), entry => entry.Event == "unexpected-exit-detected");
        }
        finally
        {
            DeletePath(path);
        }
    }

    [Fact]
    public void BeginSession_DoesNotWarnAfterCleanWindowClose()
    {
        var path = CreatePath();

        try
        {
            var first = new CompanionLifecycleStore(path);
            first.BeginSession();
            first.MarkTerminal("window-closed");

            var second = new CompanionLifecycleStore(path, isProcessAlive: _ => false);
            second.BeginSession();

            Assert.Null(second.PreviousUnexpectedExit);
        }
        finally
        {
            DeletePath(path);
        }
    }

    [Fact]
    public void FatalUnhandledException_IsReportedOnNextLaunch()
    {
        var path = CreatePath();

        try
        {
            var first = new CompanionLifecycleStore(path);
            first.BeginSession();
            first.RecordUnhandled(new InvalidOperationException("test-only"), terminating: true);

            var second = new CompanionLifecycleStore(path, isProcessAlive: _ => false);
            second.BeginSession();

            Assert.NotNull(second.PreviousUnexpectedExit);
            Assert.Equal("unhandled-exception", second.PreviousUnexpectedExit!.LastEvent);
        }
        finally
        {
            DeletePath(path);
        }
    }

    [Fact]
    public void LivePreviousProcess_DoesNotProduceCrashWarning()
    {
        var path = CreatePath();

        try
        {
            var first = new CompanionLifecycleStore(path);
            first.BeginSession();

            var second = new CompanionLifecycleStore(path, isProcessAlive: _ => true);
            second.BeginSession();

            Assert.Null(second.PreviousUnexpectedExit);
        }
        finally
        {
            DeletePath(path);
        }
    }

    [Fact]
    public void UpdateHandoff_RemainsTheTerminalReason()
    {
        var path = CreatePath();

        try
        {
            var store = new CompanionLifecycleStore(path);
            store.BeginSession();
            store.MarkTerminal("update-handoff");
            store.MarkTerminal("window-closed");

            var sessionEvents = store.ReadRecent();
            Assert.Contains(sessionEvents, entry => entry.Event == "update-handoff");
            Assert.DoesNotContain(sessionEvents, entry => entry.Event == "window-closed");
        }
        finally
        {
            DeletePath(path);
        }
    }

    [Fact]
    public void Journal_IsBounded()
    {
        var path = CreatePath();

        try
        {
            var store = new CompanionLifecycleStore(path, maxBytes: 4096);
            store.BeginSession();

            for (var i = 0; i < 500; i++)
                store.Record("heartbeat", new string('x', 200));

            Assert.True(new FileInfo(path).Length <= 4096);
            Assert.NotEmpty(store.ReadRecent());
        }
        finally
        {
            DeletePath(path);
        }
    }

    private static string CreatePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "matemcp-companion-lifecycle-tests", Guid.NewGuid().ToString("N"));
        return Path.Combine(root, "companion-lifecycle.jsonl");
    }

    private static void DeletePath(string path)
    {
        try
        {
            var root = Path.GetDirectoryName(path);
            if (root is not null && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }
}
