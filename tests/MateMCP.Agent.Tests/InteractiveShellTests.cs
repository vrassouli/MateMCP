using System.Text.Json;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Projects;
using MateMCP.Agent.Security;
using MateMCP.Agent.Tools;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace MateMCP.Agent.Tests;

public sealed class InteractiveShellTests
{
    [Fact]
    public async Task Interactive_process_supports_incremental_read_and_normal_input()
    {
        await using var manager = CreateManager();
        var started = await manager.StartAsync(PromptCommand(), WorkingDirectory(), CancellationToken.None);
        var prompt = await WaitForAsync(manager, started.SessionId, 0, s => s.Output.Contains("Prompt:", StringComparison.Ordinal));

        await manager.WriteAsync(started.SessionId, "hello", submit: true, CancellationToken.None);
        var completed = await WaitForAsync(manager, started.SessionId, prompt.NextOffset, s => s.Output.Contains("accepted:hello", StringComparison.Ordinal));

        Assert.Contains("accepted:hello", completed.Output, StringComparison.Ordinal);
        var exited = await WaitForAsync(manager, started.SessionId, completed.NextOffset, s => s.Exited);
        Assert.True(exited.Exited);
        Assert.Equal(0, exited.ExitCode);
    }

    [Fact]
    public async Task Secret_input_is_redacted_from_terminal_output_even_when_process_echoes_it()
    {
        const string secret = "mate-test-secret-12345";
        await using var manager = CreateManager();
        var started = await manager.StartAsync(PromptCommand(), WorkingDirectory(), CancellationToken.None);
        var prompt = await WaitForAsync(manager, started.SessionId, 0, s => s.Output.Contains("Prompt:", StringComparison.Ordinal));

        await manager.WriteSecretAsync(started.SessionId, secret, submit: true, CancellationToken.None);
        var completed = await WaitForAsync(manager, started.SessionId, prompt.NextOffset, s => s.Exited || s.Output.Contains("[REDACTED]", StringComparison.Ordinal));
        var final = await WaitForAsync(manager, started.SessionId, prompt.NextOffset, s => s.Exited);
        var visible = completed.Output + final.Output;

        Assert.DoesNotContain(secret, visible, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", visible, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_session_id_is_rejected_for_read_and_write()
    {
        await using var manager = CreateManager();
        Assert.Throws<KeyNotFoundException>(() => manager.Read("missing", 0));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.WriteAsync("missing", "x", true, CancellationToken.None));
    }

    [Fact]
    public async Task Session_limit_is_enforced()
    {
        await using var manager = CreateManager(maxSessions: 1);
        var first = await manager.StartAsync(WaitingCommand(), WorkingDirectory(), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(WaitingCommand(), WorkingDirectory(), CancellationToken.None));
        Assert.True(manager.Close(first.SessionId));
    }

    [Fact]
    public async Task Idle_session_is_cleaned_up()
    {
        await using var manager = CreateManager(idleSeconds: 1, lifetimeSeconds: 30);
        var started = await manager.StartAsync(WaitingCommand(), WorkingDirectory(), CancellationToken.None);
        await Task.Delay(1600);
        Assert.Throws<KeyNotFoundException>(() => manager.Read(started.SessionId, 0));
        Assert.Equal(0, manager.ActiveSessionCount);
    }

    [Fact]
    public async Task Maximum_lifetime_expires_even_when_session_is_touched()
    {
        await using var manager = CreateManager(idleSeconds: 30, lifetimeSeconds: 1);
        var started = await manager.StartAsync(WaitingCommand(), WorkingDirectory(), CancellationToken.None);
        await Task.Delay(450);
        _ = manager.Read(started.SessionId, 0);
        await Task.Delay(450);
        _ = manager.Read(started.SessionId, 0);
        await Task.Delay(450);
        Assert.Throws<KeyNotFoundException>(() => manager.Read(started.SessionId, 0));
    }

    [Fact]
    public async Task Cancelled_start_does_not_leave_an_orphan_session()
    {
        await using var manager = CreateManager();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.StartAsync(WaitingCommand(), WorkingDirectory(), cts.Token));
        Assert.Equal(0, manager.ActiveSessionCount);
    }

    [Fact]
    public async Task Credential_not_found_is_rejected_before_approval_or_resolution()
    {
        await using var manager = CreateManager();
        var started = await manager.StartAsync(WaitingCommand(), WorkingDirectory(), CancellationToken.None);
        var approvals = new FakeApprovalService(ApprovalDecision.AllowOnce);
        var tools = CreateTools(manager, new FakeCredentialStore(), approvals, NewAuditPath());

        await Assert.ThrowsAsync<McpException>(() => tools.SendSecret(started.SessionId, "missing", true, CancellationToken.None));
        Assert.Equal(0, approvals.Calls);
    }

    [Fact]
    public async Task Denied_credential_use_does_not_inject_secret()
    {
        const string secret = "must-not-be-written";
        await using var manager = CreateManager();
        var started = await manager.StartAsync(PromptCommand(), WorkingDirectory(), CancellationToken.None);
        _ = await WaitForAsync(manager, started.SessionId, 0, s => s.Output.Contains("Prompt:", StringComparison.Ordinal));
        var approvals = new FakeApprovalService(ApprovalDecision.Deny);
        var tools = CreateTools(manager, new FakeCredentialStore("ssh-prod", secret), approvals, NewAuditPath());

        await Assert.ThrowsAsync<McpException>(() => tools.SendSecret(started.SessionId, "ssh-prod", true, CancellationToken.None));
        Assert.Equal(1, approvals.Calls);
        Assert.StartsWith("ssh-prod@cmd:", approvals.LastTarget, StringComparison.Ordinal);
        Assert.False(manager.Read(started.SessionId, 0).Exited);
    }

    [Fact]
    public async Task Successful_credential_injection_never_exposes_secret_in_response_output_or_audit()
    {
        const string secret = "audit-never-see-this-secret";
        var auditPath = NewAuditPath();
        await using var manager = CreateManager();
        var started = await manager.StartAsync(PromptCommand(), WorkingDirectory(), CancellationToken.None);
        var prompt = await WaitForAsync(manager, started.SessionId, 0, s => s.Output.Contains("Prompt:", StringComparison.Ordinal));
        var approvals = new FakeApprovalService(ApprovalDecision.AllowOnce);
        var tools = CreateTools(manager, new FakeCredentialStore("server-admin-password", secret), approvals, auditPath);

        var response = await tools.SendSecret(started.SessionId, "server-admin-password", true, CancellationToken.None);
        var final = await WaitForAsync(manager, started.SessionId, prompt.NextOffset, s => s.Exited);
        var responseJson = JsonSerializer.Serialize(response);
        var auditText = await File.ReadAllTextAsync(auditPath);

        Assert.DoesNotContain(secret, responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, final.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, auditText, StringComparison.Ordinal);
        Assert.Contains("server-admin-password", responseJson, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", final.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Output_buffer_is_bounded_and_reports_truncation()
    {
        await using var manager = CreateManager(maxOutputChars: 4096);
        var started = await manager.StartAsync(LargeOutputCommand(), WorkingDirectory(), CancellationToken.None);
        var final = await WaitForAsync(manager, started.SessionId, 0, s => s.Exited);

        Assert.True(final.OutputTruncated);
        Assert.True(final.Output.Length <= 4096);
    }

    private static InteractiveShellSessionManager CreateManager(int maxSessions = 4, int idleSeconds = 30, int lifetimeSeconds = 30, int maxOutputChars = 100_000)
    {
        var options = new MateOptions
        {
            InteractiveShell = new InteractiveShellOptions
            {
                MaxSessions = maxSessions,
                IdleTimeoutSeconds = idleSeconds,
                MaxLifetimeSeconds = lifetimeSeconds,
                MaxOutputChars = maxOutputChars,
                MaxInputChars = 16_384
            }
        };
        return new InteractiveShellSessionManager(Options.Create(options));
    }

    private static InteractiveShellTools CreateTools(InteractiveShellSessionManager manager, ICredentialStore credentials, IApprovalService approvals, string auditPath)
    {
        var options = new MateOptions { RequireShellApproval = false };
        var registry = new ProjectRegistry(new StaticOptionsMonitor<MateOptions>(options));
        return new InteractiveShellTools(registry, new AuditLog(auditPath), approvals, Options.Create(options), manager, credentials);
    }

    private static async Task<ShellSessionSnapshot> WaitForAsync(InteractiveShellSessionManager manager, string sessionId, int offset, Func<ShellSessionSnapshot, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        ShellSessionSnapshot latest = manager.Read(sessionId, offset);
        while (!predicate(latest) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
            latest = manager.Read(sessionId, offset);
        }
        Assert.True(predicate(latest), $"Timed out waiting for interactive output. Last output: {latest.Output}");
        return latest;
    }

    private static string PromptCommand()
        => OperatingSystem.IsWindows()
            ? "Write-Host -NoNewline 'Prompt:'; $v=Read-Host; Write-Output \"accepted:$v\""
            : "printf 'Prompt:'; IFS= read -r v; printf 'accepted:%s\\n' \"$v\"";

    private static string WaitingCommand()
        => OperatingSystem.IsWindows()
            ? "$null=Read-Host 'Waiting'"
            : "printf 'Waiting:'; IFS= read -r v";

    private static string LargeOutputCommand()
        => OperatingSystem.IsWindows()
            ? "Write-Output ('x' * 12000)"
            : "head -c 12000 /dev/zero | tr '\\0' x";

    private static string WorkingDirectory() => Path.GetTempPath();

    private static string NewAuditPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "matemcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "audit.jsonl");
    }

    private sealed class FakeApprovalService(ApprovalDecision decision) : IApprovalService
    {
        public int Calls { get; private set; }
        public string LastTarget { get; private set; } = string.Empty;

        public Task<ApprovalDecision> RequestAsync(string capability, string target, string summary, CancellationToken cancellationToken)
        {
            Calls++;
            LastTarget = target;
            return Task.FromResult(decision);
        }
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly UserSecretInfo[] _items;
        private readonly string? _value;

        public FakeCredentialStore(string? name = null, string? value = null)
        {
            _value = value;
            _items = name is null ? [] : [new UserSecretInfo(name, "test", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CredentialKind.Password)];
        }

        public Task<IReadOnlyList<UserSecretInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<UserSecretInfo>>(_items);
        public Task SaveAsync(string name, string value, string? description, CredentialKind kind, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> ResolveAsync(string name, CancellationToken ct) => Task.FromResult(_value);
        public Task<bool> DeleteAsync(string name, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
