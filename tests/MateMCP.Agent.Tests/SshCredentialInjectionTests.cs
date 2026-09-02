using System.Text.Json;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Projects;
using MateMCP.Agent.Security;
using MateMCP.Agent.Tools;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Tests;

public sealed class SshCredentialInjectionTests
{
    [Fact]
    public async Task Generic_shell_session_injects_credential_into_real_ssh_password_prompt()
    {
        if (!OperatingSystem.IsLinux()) return;
        var host = Environment.GetEnvironmentVariable("MATEMCP_SSH_TEST_HOST");
        var user = Environment.GetEnvironmentVariable("MATEMCP_SSH_TEST_USER");
        var password = Environment.GetEnvironmentVariable("MATEMCP_SSH_TEST_PASSWORD");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password)) return;
        if (!IsSafeIdentifier(host) || !IsSafeIdentifier(user)) throw new InvalidOperationException("Unsafe SSH test host or user.");
        var port = int.TryParse(Environment.GetEnvironmentVariable("MATEMCP_SSH_TEST_PORT"), out var configuredPort)
            ? configuredPort : 22;
        if (port is < 1 or > 65535) throw new InvalidOperationException("Invalid SSH test port.");

        var auditPath = NewAuditPath();
        var options = new MateOptions { RequireShellApproval = false };
        var wrappedOptions = Options.Create(options);
        await using var sessions = new InteractiveShellSessionManager(wrappedOptions);
        var store = new SshCredentialStore(password);
        var tools = new InteractiveShellTools(
            new ProjectRegistry(new StaticOptionsMonitor<MateOptions>(options)),
            new AuditLog(auditPath), new AllowApprovalService(), wrappedOptions, sessions, store,
            new CredentialInjectionRateLimiter(wrappedOptions));
        var command = $"ssh -tt -p {port} -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null " +
                      $"-o PreferredAuthentications=password -o PubkeyAuthentication=no {user}@{host} " +
                      "\"printf 'SSH_AUTHENTICATED\\n'\"";

        var started = Assert.IsType<ShellSessionSnapshot>(await tools.Start(command));
        var prompt = await WaitForAsync(sessions, started.SessionId, 0,
            x => x.Output.Contains("password:", StringComparison.OrdinalIgnoreCase));
        var response = await tools.SendSecret(started.SessionId, "ssh-integration", true);
        var completed = await WaitForAsync(sessions, started.SessionId, prompt.NextOffset,
            x => x.Output.Contains("SSH_AUTHENTICATED", StringComparison.Ordinal));
        var audit = await File.ReadAllTextAsync(auditPath);

        Assert.Contains("SSH_AUTHENTICATED", completed.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(password, completed.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(password, JsonSerializer.Serialize(response), StringComparison.Ordinal);
        Assert.DoesNotContain(password, audit, StringComparison.Ordinal);
        Assert.Equal(1, store.ResolveCalls);
        Assert.True(sessions.Close(started.SessionId));
    }

    private static bool IsSafeIdentifier(string value) =>
        value.Length <= 253 && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_');

    private static async Task<ShellSessionSnapshot> WaitForAsync(InteractiveShellSessionManager sessions, string id,
        int offset, Func<ShellSessionSnapshot, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        var latest = sessions.Read(id, offset);
        while (!predicate(latest) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100);
            latest = sessions.Read(id, offset);
        }
        Assert.True(predicate(latest), $"Timed out waiting for SSH process. exited={latest.Exited}; exitCode={latest.ExitCode}; output={latest.Output}");
        return latest;
    }

    private static string NewAuditPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "matemcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "audit.jsonl");
    }

    private sealed class SshCredentialStore(string password) : ICredentialStore
    {
        public int ResolveCalls { get; private set; }
        public Task<IReadOnlyList<UserSecretInfo>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<UserSecretInfo>>([
                new UserSecretInfo("ssh-integration", "CI SSH credential", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            ]);
        public Task<string?> ResolveAsync(string name, CancellationToken ct)
        {
            ResolveCalls++;
            return Task.FromResult<string?>(password);
        }
        public Task SaveAsync(string name, string value, string? description, CredentialKind kind,
            IReadOnlyCollection<string>? allowedTools, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string name, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class AllowApprovalService : IApprovalService
    {
        public Task<ApprovalDecision> RequestAsync(string capability, string target, string summary,
            CancellationToken cancellationToken) => Task.FromResult(ApprovalDecision.AllowOnce);
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
