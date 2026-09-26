using System.Diagnostics;
using System.Reflection;
using MateMCP.Agent.Tools;

namespace MateMCP.Agent.Tests;

public sealed class WindowsShellEncodingTests
{
    [Fact]
    public async Task Non_interactive_windows_shell_preserves_utf8_output()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var factory = typeof(ShellTools).GetMethod(
            "CreateShellProcess",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Shell process factory was not found.");

        var psi = (ProcessStartInfo)(factory.Invoke(
            null,
            ["Write-Output 'سلام دنیا'; Write-Output 'فارسی ۱۲۳'; Write-Output 'Emoji 😀'", Path.GetTempPath()])
            ?? throw new InvalidOperationException("Shell process factory returned null."));

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Windows shell process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("سلام دنیا", stdout, StringComparison.Ordinal);
        Assert.Contains("فارسی ۱۲۳", stdout, StringComparison.Ordinal);
        Assert.Contains("Emoji 😀", stdout, StringComparison.Ordinal);
    }
}
