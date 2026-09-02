using MateMCP.Agent.Configuration;
using MateMCP.Agent.Tools;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Tests;

public sealed class InteractiveShellLifecycleTests
{
    [Fact]
    public async Task Polling_reads_do_not_keep_idle_session_alive()
    {
        await using var manager = CreateManager(idleSeconds: 1, lifetimeSeconds: 30);
        var started = await manager.StartAsync(WaitingCommand(), Path.GetTempPath(), CancellationToken.None);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        var expired = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(200);
            try
            {
                _ = manager.Read(started.SessionId, 0);
            }
            catch (KeyNotFoundException)
            {
                expired = true;
                break;
            }
        }

        Assert.True(expired, "Repeated read-only polling kept the interactive shell alive past its idle timeout.");
        Assert.Equal(0, manager.ActiveSessionCount);
    }

    [Fact]
    public async Task Actual_input_extends_idle_lifetime()
    {
        // Keep a comfortable margin around the timeout. A one-second timeout with a 700 ms
        // pre-write delay was flaky on slower arm64 CI runners before the write could be observed.
        await using var manager = CreateManager(idleSeconds: 2, lifetimeSeconds: 30);
        var started = await manager.StartAsync(WaitingCommand(), Path.GetTempPath(), CancellationToken.None);

        await Task.Delay(700);
        await manager.WriteAsync(started.SessionId, "x", submit: false, CancellationToken.None);
        await Task.Delay(900);

        var stillRunning = manager.Read(started.SessionId, 0);
        Assert.False(stillRunning.Exited);

        await Task.Delay(1_500);
        Assert.Throws<KeyNotFoundException>(() => manager.Read(started.SessionId, 0));
    }

    private static InteractiveShellSessionManager CreateManager(int idleSeconds, int lifetimeSeconds)
    {
        var options = new MateOptions
        {
            InteractiveShell = new InteractiveShellOptions
            {
                MaxSessions = 2,
                IdleTimeoutSeconds = idleSeconds,
                MaxLifetimeSeconds = lifetimeSeconds,
                MaxOutputChars = 100_000,
                MaxInputChars = 16_384
            }
        };
        return new InteractiveShellSessionManager(Options.Create(options));
    }

    private static string WaitingCommand()
        => OperatingSystem.IsWindows()
            ? "$null=Read-Host 'Waiting'"
            : "printf 'Waiting:'; IFS= read -r v";
}
