using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MateMCP.Agent.Companion.Services;

public enum AgentExecutionMode
{
    Normal,
    Elevated
}

public sealed class AgentProcessController
{
    private const string WindowsTaskName = "MateMCP Agent";

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

    public AgentExecutionMode GetConfiguredMode()
    {
        if (!OperatingSystem.IsWindows()) return AgentExecutionMode.Normal;
        var path = GetWindowsModeFile();
        if (!File.Exists(path)) return AgentExecutionMode.Normal;
        return Enum.TryParse<AgentExecutionMode>(File.ReadAllText(path).Trim(), ignoreCase: true, out var mode)
            ? mode
            : AgentExecutionMode.Normal;
    }

    public bool? IsActuallyElevated()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var process = Process.GetProcessesByName("MateMCP.Agent").FirstOrDefault();
        if (process is null) return null;
        try
        {
            if (!OpenProcessToken(process.Handle, TokenQuery, out var token)) return null;
            try
            {
                var size = Marshal.SizeOf<TokenElevation>();
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!GetTokenInformation(token, TokenInformationClass.TokenElevation, buffer, size, out _)) return null;
                    return Marshal.PtrToStructure<TokenElevation>(buffer).TokenIsElevated != 0;
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseHandle(token); }
        }
        catch { return null; }
        finally { process.Dispose(); }
    }

    public async Task SetConfiguredModeAsync(AgentExecutionMode mode, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Changing Agent privilege mode from Companion is currently supported on Windows only.");

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MateMCP");
        var script = Path.Combine(root, "configure-agent-mode-windows.ps1");
        if (!File.Exists(script))
            throw new FileNotFoundException("MateMCP Agent mode configurator is not installed.", script);

        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = root,
            WindowStyle = ProcessWindowStyle.Normal
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-Mode");
        start.ArgumentList.Add(mode.ToString());

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Agent mode configurator.");
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Agent mode configurator exited with code {process.ExitCode}.");
        await WaitForStateAsync(expectedRunning: true, ct);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning()) return;

        if (OperatingSystem.IsWindows())
        {
            if (GetConfiguredMode() == AgentExecutionMode.Elevated)
            {
                await RunAsync("schtasks.exe", ["/Run", "/TN", WindowsTaskName], ct);
                await WaitForStateAsync(expectedRunning: true, ct);
                return;
            }

            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MateMCP");
            var launcher = Path.Combine(root, "start-agent-hidden.vbs");
            if (!File.Exists(launcher))
                throw new FileNotFoundException("MateMCP Agent launcher is not installed.", launcher);
            var wscript = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wscript.exe");
            Process.Start(new ProcessStartInfo(wscript)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { launcher }
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
            if (GetConfiguredMode() == AgentExecutionMode.Elevated)
            {
                await RunAsync("schtasks.exe", ["/End", "/TN", WindowsTaskName], ct, ignoreExitCode: true);
                await WaitForStateAsync(expectedRunning: false, ct);
                return;
            }

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
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
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

    private static string GetWindowsModeFile()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MateMCP", "agent-run-mode.txt");

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

    private const uint TokenQuery = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }

    private enum TokenInformationClass
    {
        TokenElevation = 20
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, TokenInformationClass tokenInformationClass,
        IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
