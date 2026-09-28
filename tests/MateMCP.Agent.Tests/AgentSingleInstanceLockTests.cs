using MateMCP.Agent.Diagnostics;

namespace MateMCP.Agent.Tests;

public sealed class AgentSingleInstanceLockTests
{
    [Fact]
    public void TryAcquire_AllowsOnlyOneOwnerAtATime()
    {
        var root = Path.Combine(Path.GetTempPath(), "matemcp-agent-lock-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            using var first = AgentSingleInstanceLock.TryAcquire(root);
            Assert.NotNull(first);

            using var second = AgentSingleInstanceLock.TryAcquire(root);
            Assert.Null(second);

            first!.Dispose();

            using var third = AgentSingleInstanceLock.TryAcquire(root);
            Assert.NotNull(third);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
