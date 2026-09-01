using System.Diagnostics;

namespace MateMCP.Agent.Companion.Services;

public sealed class AgentProcessController
{
    public bool IsRunning()
    {
        var processes = Process.GetProcessesByName("MateMCP.Agent");
        try
        {
            return processes.Any(process =>
            {
                try { return !process.HasExited; }
                catch { return false; }
            });
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning()) return;

        if (OperatingSystem.IsWindows())
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MateMCP");
            var executable = Path.Combine(root, "MateMCP.Agent.exe");
            if (!File.Exists(executable))
                throw new FileNotFoundException("MateMCP Agent is not installed.", executable);

            Process.Start(new ProcessStartInfo(executable)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            await WaitForStateAsync(expectedRunning: true, ct);
            return;
        }

        if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var plist = Path.Combine(home, "Library", "LaunchAgents", "com.matemcp.agent.plist");
            if (!File.Exists(plist))
                throw new FileNotFoundException("MateMCP Agent LaunchAgent is not installed.", plist);

            var domain = $"gui/{GetUserId()}";
            await RunAsync("/bin/launchctl", ["bootstrap", domain, plist], ct, ignoreExitCode: true);
            await RunAsync("/bin/launchctl", ["kickstart", "-k", $"{domain}/com.matemcp.agent"], ct);
            await WaitForStateAsync(expectedRunning: true, ct);
            return;
        }

        throw new PlatformNotSupportedException("Agent lifecycle control is supported on Windows and macOS.");
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            var processes = Process.GetProcessesByName("MateMCP.Agent");
            try
            {
                foreach (var process in processes)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }
                foreach (var process in processes)
                {
                    try { await process.WaitForExitAsync(ct); }
                    catch (InvalidOperationException) { }
                }
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
            await WaitForStateAsync(expectedRunning: false, ct);
            return;
        }

        if (OperatingSystem.IsMacCatalyst() || OperatingSystem.IsMacOS())
        {
            var domain = $"gui/{GetUserId()}";
            await RunAsync("/bin/launchctl", ["bootout", $"{domain}/com.matemcp.agent"], ct, ignoreExitCode: true);
            await WaitForStateAsync(expectedRunning: false, ct);
            return;
        }

        throw new PlatformNotSupportedException("Agent lifecycle control is supported on Windows and macOS.");
    }

    public async Task RestartAsync(CancellationToken ct = default)
    {
        await StopAsync(ct);
        await Task.Delay(300, ct);
        await StartAsync(ct);
    }

    private async Task WaitForStateAsync(bool expectedRunning, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (IsRunning() == expectedRunning) return;
            await Task.Delay(200, ct);
        }
        throw new TimeoutException(expectedRunning ? "MateMCP Agent did not start in time." : "MateMCP Agent did not stop in time.");
    }

    private static async Task RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct, bool ignoreExitCode = false)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;
        if (!ignoreExitCode && process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} exited with code {process.ExitCode}: {stderr.Trim()}");
    }

    private static int GetUserId()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("/usr/bin/id", "-u")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 && int.TryParse(output.Trim(), out var uid)
            ? uid
            : throw new InvalidOperationException("Could not determine the current macOS user id.");
    }
}
