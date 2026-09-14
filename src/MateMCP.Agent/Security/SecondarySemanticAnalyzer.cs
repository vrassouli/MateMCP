using System.Net.Http.Json;
using System.Text.Json;
using MateMCP.Agent.Configuration;
using Microsoft.Extensions.Options;

namespace MateMCP.Agent.Security;

public sealed record SecondarySemanticSignal(
    ActionRiskLevel Risk,
    string Reason,
    string? SaferAlternative,
    string Source);

public interface ISecondarySemanticAnalyzer
{
    Task<SecondarySemanticSignal?> AnalyzeAsync(
        ActionAssessmentContext context,
        ActionImpactAssessment deterministicAssessment,
        CancellationToken cancellationToken = default);
}

public sealed class LocalOpenAiCompatibleSemanticAnalyzer(
    IHttpClientFactory clients,
    IOptionsMonitor<MateOptions> options,
    ILogger<LocalOpenAiCompatibleSemanticAnalyzer> logger) : ISecondarySemanticAnalyzer
{
    public async Task<SecondarySemanticSignal?> AnalyzeAsync(
        ActionAssessmentContext context,
        ActionImpactAssessment deterministicAssessment,
        CancellationToken cancellationToken = default)
    {
        var semantic = options.CurrentValue.SecondarySemanticAnalysis;
        if (!semantic.Enabled || string.IsNullOrWhiteSpace(semantic.Model)) return null;
        if (!Uri.TryCreate(semantic.Endpoint, UriKind.Absolute, out var endpoint) || !endpoint.IsLoopback)
        {
            logger.LogWarning("Secondary semantic analysis is enabled but endpoint {Endpoint} is not loopback; request was not sent.", semantic.Endpoint);
            return null;
        }

        var maxInput = Math.Clamp(semantic.MaxInputChars, 256, 8_000);
        var safeSummary = ActionImpactAssessment.RedactRemoteSummary(context.Summary);
        safeSummary = ActionImpactAssessment.Bound(safeSummary, maxInput);
        var safeTarget = ActionImpactAssessment.Bound(ActionImpactAssessment.RedactRemoteSummary(context.Target), 500);
        var prompt = $"""
            Independently review this proposed local computer action. Return ONLY one JSON object with fields:
            risk (Low|Medium|High|Critical|Unknown), reason (brief plain language), saferAlternative (optional string or null).

            This is a secondary, non-authoritative signal. Do not claim the action is safe merely because details are missing.

            Capability: {ActionImpactAssessment.Bound(context.Capability, 120)}
            Target: {safeTarget}
            Proposed action: {safeSummary}
            Deterministic MateMCP risk: {deterministicAssessment.RiskLabel}
            Deterministic effect: {ActionImpactAssessment.Bound(deterministicAssessment.Effect, 700)}
            """;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(semantic.TimeoutSeconds, 1, 20)));
            var client = clients.CreateClient();
            using var response = await client.PostAsJsonAsync(endpoint, new
            {
                model = semantic.Model,
                temperature = 0,
                messages = new object[]
                {
                    new { role = "system", content = "You are a cautious action-risk reviewer. Output JSON only." },
                    new { role = "user", content = prompt }
                }
            }, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("Secondary semantic analyzer returned {StatusCode}; deterministic analysis remains in effect.", response.StatusCode);
                return null;
            }

            var envelope = await response.Content.ReadFromJsonAsync<ChatCompletionEnvelope>(cancellationToken: timeout.Token);
            var content = envelope?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content)) return null;
            content = StripCodeFence(content.Trim());
            var payload = JsonSerializer.Deserialize<SemanticPayload>(content, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (payload is null || !TryParseRisk(payload.Risk, out var risk) || string.IsNullOrWhiteSpace(payload.Reason)) return null;

            return new SecondarySemanticSignal(
                risk,
                ActionImpactAssessment.Bound(payload.Reason, 500),
                string.IsNullOrWhiteSpace(payload.SaferAlternative) ? null : ActionImpactAssessment.Bound(payload.SaferAlternative, 500),
                $"local-semantic:{ActionImpactAssessment.Bound(semantic.Model, 80)}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("Secondary semantic analysis timed out; deterministic analysis remains in effect.");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            logger.LogInformation(ex, "Secondary semantic analysis unavailable; deterministic analysis remains in effect.");
            return null;
        }
    }

    private static bool TryParseRisk(string? value, out ActionRiskLevel risk)
        => Enum.TryParse(value, ignoreCase: true, out risk);

    private static string StripCodeFence(string value)
    {
        if (!value.StartsWith("```", StringComparison.Ordinal)) return value;
        var firstLine = value.IndexOf('\n');
        var lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine >= 0 && lastFence > firstLine ? value[(firstLine + 1)..lastFence].Trim() : value;
    }

    private sealed record SemanticPayload(string? Risk, string? Reason, string? SaferAlternative);
    private sealed record ChatMessage(string? Content);
    private sealed record ChatChoice(ChatMessage? Message);
    private sealed record ChatCompletionEnvelope(IReadOnlyList<ChatChoice>? Choices);
}
