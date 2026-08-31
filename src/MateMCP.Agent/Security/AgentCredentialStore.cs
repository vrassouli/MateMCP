using System.Diagnostics;

namespace MateMCP.Agent.Security;

public sealed class AgentCredentialStore
{
    private const string Service = "MateMCP.Agent";

    public async Task SaveAsync(string agentId, string credential, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Secure Agent credential storage is currently implemented for macOS Keychain.");
        using var process = NewSecurityProcess("add-generic-password", "-U", "-s", Service, "-a", agentId, "-w", credential);
        process.Start(); await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new InvalidOperationException("Could not save the Agent credential in macOS Keychain.");
    }

    public async Task<string?> GetAsync(string agentId, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        using var process = NewSecurityProcess("find-generic-password", "-s", Service, "-a", agentId, "-w");
        process.Start(); var value = await process.StandardOutput.ReadToEndAsync(ct); await process.WaitForExitAsync(ct);
        return process.ExitCode == 0 ? value.Trim() : null;
    }

    private static Process NewSecurityProcess(params string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return new Process { StartInfo = start };
    }
}
