using System.Text.Json;
using MateMCP.Agent.Audit;
using MateMCP.Agent.Security;

namespace MateMCP.Agent.Tests;

public sealed class AuditApprovalAssessmentTests
{
    [Fact]
    public async Task WriteApprovalAsync_RetainsStructuredAssessment_AndRedactsActionSecrets()
    {
        var path = TempAuditPath();
        try
        {
            var audit = new AuditLog(path);
            var assessment = new ActionImpactAssessment(
                ActionRiskLevel.High,
                AssessmentConfidence.High,
                "credential-access",
                "May authenticate to a remote service.",
                ["project:Demo"],
                "project:Demo",
                Destructive: false,
                Reversible: ActionReversibility.Unknown,
                RequiresElevation: false,
                CredentialExposure: true,
                NetworkEffect: true,
                PersistenceEffect: false,
                ProductionLikelihood: true,
                Reasons: ["A credential-like argument and production target were detected."],
                SaferAlternative: "Use a scoped credential and verify the endpoint first.",
                Preview: "Remote endpoint referenced: https://example.invalid",
                Contributors:
                [
                    new AssessmentContributor("builtin-deterministic", "deterministic", true),
                    new AssessmentContributor("local-semantic:qwen", "semantic", false)
                ]);

            await audit.WriteApprovalAsync(
                "approval.assessment",
                "shell.exec:project:Demo",
                "curl https://example.invalid --token super-secret-token",
                "analyzed:policy=requireapproval",
                assessment);

            var entry = Assert.Single(await audit.ReadAsync());
            Assert.Equal("approval.assessment", entry.Capability);
            Assert.NotNull(entry.ProposedAction);
            Assert.DoesNotContain("super-secret-token", entry.ProposedAction, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", entry.ProposedAction, StringComparison.Ordinal);

            var stored = Assert.IsType<AuditImpactAssessment>(entry.Assessment);
            Assert.Equal("High", stored.Risk);
            Assert.Equal("credential-access", stored.IntentCategory);
            Assert.Equal("May authenticate to a remote service.", stored.Effect);
            Assert.True(stored.CredentialExposure);
            Assert.True(stored.NetworkEffect);
            Assert.True(stored.ProductionLikelihood);
            Assert.Contains(stored.Reasons, x => x.Contains("credential-like", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(stored.Contributors!, x => x.Source == "builtin-deterministic" && x.Authoritative);
            Assert.Contains(stored.Contributors!, x => x.Source == "local-semantic:qwen" && !x.Authoritative);
        }
        finally { DeleteAudit(path); }
    }

    [Fact]
    public async Task LegacyAuditEntry_WithoutAssessmentFields_RemainsReadable()
    {
        var path = TempAuditPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var legacy = JsonSerializer.Serialize(new
            {
                Timestamp = DateTimeOffset.UtcNow,
                Capability = "shell.exec",
                Target = "project:Legacy",
                Result = "exit:0",
                Credential = (string?)null,
                Tool = (string?)null
            });
            await File.WriteAllTextAsync(path, legacy + Environment.NewLine);

            var audit = new AuditLog(path);
            var entry = Assert.Single(await audit.ReadAsync());

            Assert.Equal("shell.exec", entry.Capability);
            Assert.Null(entry.ProposedAction);
            Assert.Null(entry.Assessment);
        }
        finally { DeleteAudit(path); }
    }

    [Fact]
    public async Task WriteApprovalAsync_BoundsLargeAssessmentFields()
    {
        var path = TempAuditPath();
        try
        {
            var audit = new AuditLog(path);
            var assessment = new ActionImpactAssessment(
                ActionRiskLevel.Unknown,
                AssessmentConfidence.Low,
                new string('c', 500),
                new string('e', 5_000),
                [new string('r', 2_000)],
                new string('s', 2_000),
                false,
                ActionReversibility.Unknown,
                false,
                false,
                false,
                false,
                false,
                [new string('w', 2_000)],
                Preview: new string('p', 10_000));

            await audit.WriteApprovalAsync(
                "approval.assessment",
                new string('t', 2_000),
                new string('a', 10_000),
                new string('x', 3_000),
                assessment);

            var entry = Assert.Single(await audit.ReadAsync());
            Assert.True(entry.Target.Length <= 301);
            Assert.True(entry.Result.Length <= 1_001);
            Assert.True(entry.ProposedAction!.Length <= 1_201);
            Assert.True(entry.Assessment!.Effect.Length <= 601);
            Assert.True(entry.Assessment.Preview!.Length <= 2_001);
            Assert.True(entry.Assessment.Reasons[0].Length <= 301);
        }
        finally { DeleteAudit(path); }
    }

    private static string TempAuditPath()
        => Path.Combine(Path.GetTempPath(), "matemcp-audit-tests", Guid.NewGuid().ToString("n"), "audit.jsonl");

    private static void DeleteAudit(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch { }
    }
}
