using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MateMCP.Agent.Security;

public sealed record LocalAccessCredential(string Token);

public sealed class AgentCredentialStore
{
    private const string Service = "MateMCP.Agent";
    private const string LocalAccessAccount = "local-access-token";
    private const string AgentAccountPrefix = "agent:";

    public Task SaveAsync(string agentId, string credential, CancellationToken ct)
        => SaveSecretAsync(AgentAccountPrefix + agentId, credential, ct);

    public Task<string?> GetAsync(string agentId, CancellationToken ct)
        => GetSecretAsync(AgentAccountPrefix + agentId, ct);

    public async Task<string> ResolveLocalAccessTokenAsync(string? configuredToken, string configurationPath, CancellationToken ct)
    {
        // Explicit environment overrides are intentionally ephemeral and are never persisted.
        var environmentToken = Environment.GetEnvironmentVariable("MATEMCP_Mate__AccessToken");
        if (!string.IsNullOrWhiteSpace(environmentToken)) return environmentToken;

        if (!OperatingSystem.IsMacOS())
        {
            if (!string.IsNullOrWhiteSpace(configuredToken) && configuredToken != "change-me-before-exposing")
                return configuredToken;
            return "matemcp_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        }

        var existing = await GetSecretAsync(LocalAccessAccount, ct);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            RemovePlaintextAccessToken(configurationPath);
            return existing;
        }

        var token = !string.IsNullOrWhiteSpace(configuredToken) && configuredToken != "change-me-before-exposing"
            ? configuredToken
            : "matemcp_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        await SaveSecretAsync(LocalAccessAccount, token, ct);
        RemovePlaintextAccessToken(configurationPath);
        return token;
    }

    private static async Task SaveSecretAsync(string account, string credential, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Secure credential storage is currently implemented for macOS Keychain.");

        using var process = NewSecurityProcess("add-generic-password", "-U", "-s", Service, "-a", account, "-w", credential);
        process.Start();
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"Could not save a MateMCP credential in macOS Keychain: {error.Trim()}");
        }
    }

    private static async Task<string?> GetSecretAsync(string account, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        using var process = NewSecurityProcess("find-generic-password", "-s", Service, "-a", account, "-w");
        process.Start();
        var value = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return process.ExitCode == 0 ? value.Trim() : null;
    }

    private static void RemovePlaintextAccessToken(string path)
    {
        if (!File.Exists(path)) return;
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
        var mate = root?["Mate"]?.AsObject();
        if (root is null || mate is null || mate["AccessToken"] is null) return;

        mate.Remove("AccessToken");
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static Process NewSecurityProcess(params string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/security")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return new Process { StartInfo = start };
    }
}
