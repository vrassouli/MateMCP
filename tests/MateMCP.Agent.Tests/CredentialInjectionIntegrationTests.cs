using System.Diagnostics;
using System.Text.Json;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Projects;
using MateMCP.Agent.Security;
using MateMCP.Agent.Tools;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace MateMCP.Agent.Tests;

public sealed class CredentialInjectionIntegrationTests
{
    private const string CredentialName = "test-password";
    private const string ValidSecret = "mate-test-secret-12345";

    [Fact]
    public async Task Shell_session_send_secret_resolves_locally_and_continues_process()
    {
        var auditPath = NewAuditPath();
        await using var sessions = CreateManager();
        var store = new TrackingCredentialStore(CredentialName, ValidSecret, [UserSecretInfo.ShellSessionSendSecretTool]);
        var tools = CreateTools(sessions, store, new FakeApprovalService(ApprovalDecision.AllowOnce), auditPath);

        var started = await StartCredentialPromptAsync(sessions);
        var prompt = await WaitForAsync(sessions, started.SessionId, 0,
            snapshot => snapshot.Output.Contains("Password:", StringComparison.Ordinal));

        var response = await tools.SendSecret(started.SessionId, CredentialName, true);
        var completed = await WaitForAsync(sessions, started.SessionId, prompt.NextOffset,
            snapshot => snapshot.Output.Contains("AUTHENTICATED", StringComparison.Ordinal));
        var responseJson = JsonSerializer.Serialize(response);
        var auditText = await File.ReadAllTextAsync(auditPath);

        Assert.Equal(1, store.ResolveCalls);
        Assert.Equal(CredentialName, store.LastResolvedName);
        Assert.Contains("AUTHENTICATED", completed.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(ValidSecret, completed.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(ValidSecret, responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(ValidSecret, auditText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Credential_tool_policy_rejects_write_only_credential_before_approval_or_resolution()
    {
        var auditPath = NewAuditPath();
        await using var sessions = CreateManager();
        var store = new TrackingCredentialStore(CredentialName, ValidSecret, ["shell_session_write"]);
        var approvals = new FakeApprovalService(ApprovalDecision.AllowOnce);
        var tools = CreateTools(sessions, store, approvals, auditPath);
        var started = await StartCredentialPromptAsync(sessions);
        _ = await WaitForAsync(sessions, started.SessionId, 0,
            snapshot => snapshot.Output.Contains("Password:", StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.SendSecret(started.SessionId, CredentialName, true));
        var auditText = await ReadAuditAsync(auditPath);

        Assert.Contains("not authorized", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, approvals.Calls);
        Assert.Equal(0, store.ResolveCalls);
        Assert.Contains("denied:tool-policy", auditText, StringComparison.Ordinal);
        Assert.DoesNotContain(ValidSecret, auditText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_value_is_not_returned_in_response_or_audit_log()
    {
        var auditPath = NewAuditPath();
        await using var sessions = CreateManager();
        var tools = CreateTools(sessions, new TrackingCredentialStore(CredentialName, ValidSecret),
            new FakeApprovalService(ApprovalDecision.AllowOnce), auditPath);
        var started = await StartCredentialPromptAsync(sessions);
        _ = await WaitForAsync(sessions, started.SessionId, 0,
            snapshot => snapshot.Output.Contains("Password:", StringComparison.Ordinal));

        var response = await tools.SendSecret(started.SessionId, CredentialName, true);
        var responseJson = JsonSerializer.Serialize(response);
        var auditText = await File.ReadAllTextAsync(auditPath);

        Assert.DoesNotContain(ValidSecret, responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain(ValidSecret, auditText, StringComparison.Ordinal);
        Assert.Contains(CredentialName, responseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Secret_value_is_not_in_exception_message_when_injection_fails()
    {
        var auditPath = NewAuditPath();
        await using var sessions = CreateManager(maxInputChars: 8);
        var tools = CreateTools(sessions, new TrackingCredentialStore(CredentialName, ValidSecret),
            new FakeApprovalService(ApprovalDecision.AllowOnce), auditPath);
        var started = await StartCredentialPromptAsync(sessions);
        _ = await WaitForAsync(sessions, started.SessionId, 0,
            snapshot => snapshot.Output.Contains("Password:", StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.SendSecret(started.SessionId, CredentialName, true));

        Assert.DoesNotContain(ValidSecret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ValidSecret, await ReadAuditAsync(auditPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Credential_not_found_does_not_resolve_or_inject()
    {
        await using var sessions = CreateManager();
        var store = new TrackingCredentialStore();
        var approvals = new FakeApprovalService(ApprovalDecision.AllowOnce);
        var tools = CreateTools(sessions, store, approvals, NewAuditPath());
        var started = await StartCredentialPromptAsync(sessions);
        _ = await WaitForAsync(sessions, started.SessionId, 0,
            snapshot => snapshot.Output.Contains("Password:", StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.SendSecret(started.SessionId, "missing-credential", true));

        Assert.Equal(0, store.ResolveCalls);
        Assert.Equal(0, approvals.Calls);
        Assert.DoesNotContain(ValidSecret, exception.Message, StringComparison.Ordinal);
        Assert.False(sessions.Read(started.SessionId, 0).Exited);
    }

    [Fact]
    public async Task Unauthorized_credential_does_not_resolve_or_inject()
    {
        var auditPath = NewAuditPath();
        await using var sessions = CreateManager();
        var store = new TrackingCredentialStore(CredentialName, ValidSecret);
        var approvals = new FakeApprovalService(ApprovalDecision.Deny);
        var tools = CreateTools(sessions, store, approvals, auditPath);
        var started = await StartCredentialPromptAsync(sessions);
        _ = await WaitForAsync(sessions, started.SessionId, 0,
            snapshot => snapshot.Output.Contains("Password:", StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.SendSecret(started.SessionId, CredentialName, true));

        Assert.Equal(1, approvals.Calls);
        Assert.Equal(0, store.ResolveCalls);
        Assert.DoesNotContain(ValidSecret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ValidSecret, await File.ReadAllTextAsync(auditPath), StringComparison.Ordinal);
        Assert.False(sessions.Read(started.SessionId, 0).Exited);
    }

    [Fact]
    public async Task Invalid_session_id_is_rejected_before_credential_lookup()
    {
        await using var sessions = CreateManager();
        var store = new TrackingCredentialStore(CredentialName, ValidSecret);
        var approvals = new FakeApprovalService(ApprovalDecision.AllowOnce);
        var tools = CreateTools(sessions, store, approvals, NewAuditPath());

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.SendSecret("invalid-session-id", CredentialName, true));

        Assert.Equal(0, store.ListCalls);
        Assert.Equal(0, store.ResolveCalls);
        Assert.Equal(0, approvals.Calls);
        Assert.DoesNotContain(ValidSecret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closed_session_rejects_credential_injection()
    {
        await using var sessions = CreateManager();
        var store = new TrackingCredentialStore(CredentialName, ValidSecret);
        var approvals = new FakeApprovalService(ApprovalDecision.AllowOnce);
        var tools = CreateTools(sessions, store, approvals, NewAuditPath());
        var started = await StartCredentialPromptAsync(sessions);
        Assert.True(sessions.Close(started.SessionId));

        var exception = await Assert.ThrowsAsync<McpException>(
            () => tools.SendSecret(started.SessionId, CredentialName, true));

        Assert.Equal(0, store.ListCalls);
        Assert.Equal(0, store.ResolveCalls);
        Assert.Equal(0, approvals.Calls);
        Assert.DoesNotContain(ValidSecret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_credential_is_injected_but_process_returns_denied_without_exposure()
    {
        const string invalidSecret = "mate-test-invalid-67890";
        await using var sessions = CreateManager();
        var store = new TrackingCredentialStore(CredentialName, invalidSecret);
        var tools = CreateTools(sessions, store, new FakeApprovalService(ApprovalDecision.AllowOnce), NewAuditPath());
        var started = await StartCredentialPromptAsync(sessions);
        var prompt = await WaitForAsync(sessions, started.SessionId, 0,
            snapshot => snapshot.Output.Contains("Password:", StringComparison.Ordinal));

        _ = await tools.SendSecret(started.SessionId, CredentialName, true);
        var completed = await WaitForAsync(sessions, started.SessionId, prompt.NextOffset,
            snapshot => snapshot.Output.Contains("DENIED", StringComparison.Ordinal));

        Assert.Equal(1, store.ResolveCalls);
        Assert.Contains("DENIED", completed.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(invalidSecret, completed.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disposing_session_manager_terminates_orphan_process()
    {
        var sessions = CreateManager();
        var started = await StartCredentialPromptAsync(sessions);
        _ = await WaitForAsync(sessions, started.SessionId, 0,
            snapshot => snapshot.Output.Contains("Password:", StringComparison.Ordinal));
        using var process = Process.GetProcessById(started.ProcessId);

        await sessions.DisposeAsync();

        var exited = await WaitForProcessExitAsync(process, TimeSpan.FromSeconds(8));
        Assert.True(exited, $"Interactive child process {started.ProcessId} was not terminated during manager disposal.");
    }

    private static InteractiveShellSessionManager CreateManager(int maxInputChars = 16_384)
    {
        var options = new MateOptions
        {
            InteractiveShell = new InteractiveShellOptions
            {
                MaxSessions = 4,
                IdleTimeoutSeconds = 30,
                MaxLifetimeSeconds = 30,
                MaxOutputChars = 100_000,
                MaxInputChars = maxInputChars
            }
        };
        return new InteractiveShellSessionManager(Options.Create(options));
    }

    private static InteractiveShellTools CreateTools(InteractiveShellSessionManager sessions, ICredentialStore credentials,
        IApprovalService approvals, string auditPath)
    {
        var options = new MateOptions { RequireShellApproval = false };
        var optionWrapper = Options.Create(options);
        return new InteractiveShellTools(new ProjectRegistry(new StaticOptionsMonitor<MateOptions>(options)),
            new AuditLog(auditPath), approvals, optionWrapper, sessions, credentials,
            new CredentialInjectionRateLimiter(optionWrapper));
    }

    private static async Task<ShellSessionSnapshot> StartCredentialPromptAsync(InteractiveShellSessionManager sessions)
    {
        var assemblyPath = CredentialPromptAssemblyPath();
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("The credential prompt test process was not built.", assemblyPath);

        var command = $"dotnet exec \"{assemblyPath}\"";
        var started = await sessions.StartAsync(command, Path.GetDirectoryName(assemblyPath)!, CancellationToken.None);
        Assert.True(started.ProcessId > 0);
        return started;
    }

    private static string CredentialPromptAssemblyPath()
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = testOutput.Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the test build configuration.");
        var repositoryRoot = FindRepositoryRoot(testOutput);
        return Path.Combine(repositoryRoot.FullName, "tests", "MateMCP.Agent.CredentialPrompt", "bin",
            configuration, "net10.0", "MateMCP.Agent.CredentialPrompt.dll");
    }

    private static DirectoryInfo FindRepositoryRoot(DirectoryInfo start)
    {
        for (var current = start; current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "MateMCP.slnx"))) return current;
        }
        throw new DirectoryNotFoundException("Could not locate the MateMCP repository root.");
    }

    private static async Task<ShellSessionSnapshot> WaitForAsync(InteractiveShellSessionManager manager, string id,
        int offset, Func<ShellSessionSnapshot, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
        var latest = manager.Read(id, offset);
        while (!predicate(latest) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50);
            latest = manager.Read(id, offset);
        }

        if (predicate(latest)) return latest;
        throw new TimeoutException(
            $"Timed out waiting for interactive process output. pid={latest.ProcessId}; exited={latest.Exited}; exitCode={latest.ExitCode}; nextOffset={latest.NextOffset}.");
    }

    private static async Task<bool> WaitForProcessExitAsync(Process process, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (process.HasExited) return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            await Task.Delay(50);
        }
        return process.HasExited;
    }

    private static string NewAuditPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "matemcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "audit.jsonl");
    }

    private static Task<string> ReadAuditAsync(string path)
        => File.Exists(path) ? File.ReadAllTextAsync(path) : Task.FromResult(string.Empty);

    private sealed class TrackingCredentialStore : ICredentialStore
    {
        private readonly UserSecretInfo[] _items;
        private readonly string? _value;

        public TrackingCredentialStore(string? name = null, string? value = null, IReadOnlyList<string>? allowedTools = null)
        {
            _value = value;
            _items = name is null ? [] :
                [new UserSecretInfo(name, "integration test credential", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, CredentialKind.Password, allowedTools)];
        }

        public int ListCalls { get; private set; }
        public int ResolveCalls { get; private set; }
        public string? LastResolvedName { get; private set; }

        public Task<IReadOnlyList<UserSecretInfo>> ListAsync(CancellationToken ct)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<UserSecretInfo>>(_items);
        }

        public Task<string?> ResolveAsync(string name, CancellationToken ct)
        {
            ResolveCalls++;
            LastResolvedName = name;
            return Task.FromResult(_value);
        }

        public Task SaveAsync(string name, string value, string? description, CredentialKind kind,
            IReadOnlyCollection<string>? allowedTools, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string name, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeApprovalService(ApprovalDecision decision) : IApprovalService
    {
        public int Calls { get; private set; }

        public Task<ApprovalDecision> RequestAsync(string capability, string target, string summary,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(decision);
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
