using MateMCP.Agent.Security;

namespace MateMCP.Agent.Tests;

public sealed class ActionImpactAssessmentRemoteSummaryTests
{
    private static readonly ActionImpactAssessment Assessment = new(
        ActionRiskLevel.High,
        AssessmentConfidence.High,
        "credential-access",
        "Uses credential material.",
        ["project:Demo"],
        "project:Demo",
        Destructive: false,
        Reversible: ActionReversibility.Unknown,
        RequiresElevation: false,
        CredentialExposure: true,
        NetworkEffect: false,
        PersistenceEffect: false,
        ProductionLikelihood: false,
        Reasons: ["Credential handling was detected."]);

    [Theory]
    [InlineData("tool --password super-secret", "super-secret")]
    [InlineData("tool -token abc123", "abc123")]
    [InlineData("tool /client-secret \"quoted secret\"", "quoted secret")]
    [InlineData("Authorization: Bearer eyJ.secret.value", "eyJ.secret.value")]
    [InlineData("api_key=very-secret-value", "very-secret-value")]
    public void ToRemoteSummary_RedactsCredentialValues(string summary, string secret)
    {
        var remote = Assessment.ToRemoteSummary(summary);

        Assert.DoesNotContain(secret, remote, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", remote, StringComparison.Ordinal);
        Assert.Contains("Risk: High", remote, StringComparison.Ordinal);
        Assert.Contains("Expected effect:", remote, StringComparison.Ordinal);
    }

    [Fact]
    public void ToRemoteSummary_PreservesNonSecretTechnicalContext()
    {
        var remote = Assessment.ToRemoteSummary("curl https://example.test --token secret-value --request POST");

        Assert.Contains("curl https://example.test", remote, StringComparison.Ordinal);
        Assert.Contains("--request POST", remote, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", remote, StringComparison.Ordinal);
    }
}
