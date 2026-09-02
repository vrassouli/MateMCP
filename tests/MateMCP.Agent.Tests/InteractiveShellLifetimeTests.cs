using MateMCP.Agent.Configuration;
using MateMCP.Agent.Tools;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Tests;

public sealed class InteractiveShellLifetimeTests
{
    [Fact]
    public async Task Repeated_reads_keep_an_active_interactive_session_alive()
    {
        var options = new MateOptions
        {
            InteractiveShell = new InteractiveShellOptions
            {
                MaxSessions = 2,
                IdleTimeoutSeconds = 1,
                MaxLifetimeSeconds = 30,
                MaxOutputChars = 100_000,
                MaxInputChars = 16_384
            }
        };

        await using var manager = new InteractiveShellSessionManager(Options.Create(options));
        var started = await manager.StartAsync(WaitingCommand(), Path.GetTempPath(), CancellationToken.None);

        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(400);
            _ = manager.Read(started.SessionId, 0);
        }

        Assert.Equal(1, manager.ActiveSessionCount);
        Assert.False(manager.Read(started.SessionId, 0).Exited);
        Assert.True(manager.Close(started.SessionId));
    }

    private static string WaitingCommand()
        => OperatingSystem.IsWindows()
            ? "$null=Read-Host 'Waiting'"
            : "printf 'Waiting:'; IFS= read -r v";
}
