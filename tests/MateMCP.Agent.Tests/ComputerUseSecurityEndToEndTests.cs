using MateMCP.Agent.Audit;
using MateMCP.Agent.Configuration;
using MateMCP.Agent.Desktop;
using MateMCP.Agent.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Tests;

public sealed class ComputerUseSecurityEndToEndTests
{
    [Fact]
    public async Task Sensitive_approval_audit_and_local_stop_work_end_to_end()
    {
        var root = Path.Combine(Path.GetTempPath(), "MateMCP", "security-e2e", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var auditPath = Path.Combine(root, "audit.jsonl");
            var policyPath = Path.Combine(root, "approval-policies.json");
            var options = new MateOptions
            {
                ApprovalTimeoutSeconds = 30,
                Relay = new RelayOptions { Enabled = false }
            };
            var audit = new AuditLog(auditPath);
            var presence = new CompanionNotificationPresence();
            presence.MarkReady();
            var notifications = new LocalNotificationService(
                NullLogger<LocalNotificationService>.Instance,
                presence);
            var approvals = new ApprovalService(
                new StaticOptionsMonitor<MateOptions>(options),
                new StaticHttpClientFactory(),
                new AgentCredentialStore(),
                new ApprovalPolicyStore(policyPath),
                audit,
                notifications,
                NullLogger<ApprovalService>.Instance);

            const string privateSentinel = "PRIVATE-SUMMARY-SENTINEL";
            var assessment = ComputerUseRiskClassifier.AssessSemantic(
                "invoke", role: "button", name: "Delete device", identifier: "deleteDevice");
            Assert.Equal(ComputerUseRiskLevel.High, assessment.Level);
            var target = ComputerUseRiskClassifier.PolicyTarget(
                "semantic-action", "invoke", "role=button,name=Delete device,automationId=deleteDevice", assessment);

            var request = approvals.RequestComputerUseAsync(
                "desktop.semantic",
                target,
                $"Delete the selected device. {privateSentinel}",
                assessment,
                CancellationToken.None);

            var pending = await WaitForPendingAsync(approvals);
            Assert.Equal("High", pending.Risk);
            Assert.Equal(assessment.Effect, pending.Effect);
            Assert.Contains("Delete the selected device", pending.Summary, StringComparison.Ordinal);
            Assert.True(approvals.Decide(pending.Id, ApprovalDecision.AllowOnce));
            Assert.Equal(ApprovalDecision.AllowOnce, await request);
            Assert.Empty(approvals.GetPending());

            var entries = await audit.ReadAsync();
            var approval = Assert.Single(entries, entry => entry.Capability == "approval");
            Assert.Contains("decision:allowonce", approval.Result, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("risk=high", approval.Result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(privateSentinel, approval.Target, StringComparison.Ordinal);

            var analyzed = Assert.Single(entries, entry => entry.Capability == "approval.assessment");
            Assert.NotNull(analyzed.ProposedAction);
            Assert.Contains(privateSentinel, analyzed.ProposedAction, StringComparison.Ordinal);
            Assert.NotNull(analyzed.Assessment);
            Assert.Equal("High", analyzed.Assessment.Risk);
            Assert.Equal(assessment.Effect, analyzed.Assessment.Effect);

            var sessions = new ComputerUseSessionManager(Path.Combine(root, "computer-use"));
            var active = sessions.Touch("semantic-input", "Delete device");
            Assert.True(active.Active);
            Assert.False(active.Blocked);
            Assert.NotNull(active.SessionId);

            var stopped = sessions.Stop("E2E local stop");
            Assert.True(stopped.Blocked);
            Assert.False(stopped.Active);
            Assert.Throws<InvalidOperationException>(() => sessions.EnsureAvailable());

            var indicatorPath = Path.Combine(root, "computer-use", ComputerUseSessionManager.IndicatorFileName);
            var indicator = await File.ReadAllTextAsync(indicatorPath);
            Assert.Contains("\"blocked\": true", indicator, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Delete device", indicator, StringComparison.Ordinal);
            Assert.DoesNotContain("E2E local stop", indicator, StringComparison.Ordinal);
            Assert.DoesNotContain(active.SessionId!, indicator, StringComparison.Ordinal);

            var resumed = sessions.Resume();
            Assert.False(resumed.Blocked);
            Assert.False(resumed.Active);
            Assert.Null(resumed.SessionId);
            sessions.EnsureAvailable();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task<PendingApproval> WaitForPendingAsync(ApprovalService approvals)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var pending = approvals.GetPending();
            if (pending.Count == 1) return pending.Single();
            await Task.Delay(20);
        }
        throw new TimeoutException("Approval did not become pending within 5 seconds.");
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
