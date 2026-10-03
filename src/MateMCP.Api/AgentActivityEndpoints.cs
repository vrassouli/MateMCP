using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MateMCP.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MateMCP.Api;

public static partial class AgentActivityEndpoints
{
    public const int RetainedRuntimeEventsPerAgent = 500;
    private const string RuntimePrefix = "runtime.";

    public static void Map(WebApplication app, string internalKey)
    {
        app.MapPost("/internal/agents/activity", async (
            HttpContext context,
            AgentActivityReport report,
            ControlPlaneDbContext db) =>
        {
            if (!SecretEquals(context.Request.Headers["X-MateMCP-Internal-Key"].ToString(), internalKey))
                return Results.Unauthorized();

            var type = NormalizeType(report.Type);
            if (type is null || string.IsNullOrWhiteSpace(report.AgentId))
                return Results.BadRequest(new { error = "invalid_activity" });

            var agent = await db.Agents
                .Include(x => x.Owner)
                .SingleOrDefaultAsync(
                    x => x.PublicId == report.AgentId &&
                         !x.IsRevoked &&
                         x.Owner != null &&
                         !x.Owner.IsDisabled,
                    context.RequestAborted);

            if (agent is null)
                return Results.NotFound();

            var status = NormalizeStatus(report.Status);
            var level = NormalizeLevel(report.Level, status);
            var stored = new StoredAgentActivity(
                Version: 1,
                Status: status,
                Level: level,
                Operation: SanitizeText(report.Operation, 120),
                Message: SanitizeText(report.Message, 220),
                DurationMs: NormalizeDuration(report.DurationMs),
                RequestId: SanitizeIdentifier(report.RequestId, 96),
                Project: SanitizeProject(report.Project));

            db.AuditEvents.Add(new AuditEvent
            {
                UserId = agent.OwnerId,
                AgentDeviceId = agent.Id,
                EventType = RuntimePrefix + type,
                Detail = JsonSerializer.Serialize(stored),
                CreatedAt = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(context.RequestAborted);
            await PruneRuntimeEventsAsync(db, agent.Id, context.RequestAborted);
            return Results.Ok();
        });
    }

    public static AgentActivityEntry? ToEntry(AuditEvent auditEvent)
    {
        if (auditEvent.EventType.StartsWith(RuntimePrefix, StringComparison.Ordinal))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<StoredAgentActivity>(auditEvent.Detail);
                if (stored is null) return null;

                var type = auditEvent.EventType[RuntimePrefix.Length..];
                return new AgentActivityEntry(
                    Category: type switch
                    {
                        "connected" or "disconnected" => "connection",
                        "request" => "request",
                        _ => "runtime"
                    },
                    Type: type,
                    Status: NormalizeStatus(stored.Status),
                    Level: NormalizeLevel(stored.Level, stored.Status),
                    Operation: SanitizeText(stored.Operation, 120),
                    Message: SanitizeText(stored.Message, 220),
                    DurationMs: NormalizeDuration(stored.DurationMs),
                    RequestId: SanitizeIdentifier(stored.RequestId, 96),
                    CreatedAt: auditEvent.CreatedAt,
                    Project: SanitizeProject(stored.Project));
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return auditEvent.EventType switch
        {
            "agent.enrolled" => new("security", "enrolled", "success", "info", "Device enrolled", "Device access was enrolled.", null, null, auditEvent.CreatedAt),
            "agent.credential_rotated" => new("security", "credential_rotated", "success", "info", "Credential rotated", "Device credentials were recovered or rotated.", null, null, auditEvent.CreatedAt),
            "agent.revoked" or "agent.revoked.by-device" or "admin.agent.revoked" => new("security", "revoked", "warning", "warning", "Access revoked", "Device access was revoked.", null, null, auditEvent.CreatedAt),
            "approval.allowed" => new("approval", "approval_allowed", "success", "info", "Approval allowed", "A pending Agent operation was approved.", null, null, auditEvent.CreatedAt),
            "approval.denied" => new("approval", "approval_denied", "failure", "warning", "Approval denied", "A pending Agent operation was denied.", null, null, auditEvent.CreatedAt),
            _ => null
        };
    }

    private static async Task PruneRuntimeEventsAsync(ControlPlaneDbContext db, Guid agentDeviceId, CancellationToken cancellationToken)
    {
        var stale = await db.AuditEvents
            .Where(x => x.AgentDeviceId == agentDeviceId && x.EventType.StartsWith(RuntimePrefix))
            .OrderByDescending(x => x.Id)
            .Skip(RetainedRuntimeEventsPerAgent)
            .Take(250)
            .ToListAsync(cancellationToken);

        if (stale.Count == 0) return;

        db.AuditEvents.RemoveRange(stale);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? NormalizeType(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "connected" => "connected",
            "disconnected" => "disconnected",
            "request" => "request",
            _ => null
        };

    private static string NormalizeStatus(string? value)
        => value?.Trim().ToLowerInvariant() switch
        {
            "success" => "success",
            "warning" => "warning",
            "failure" or "failed" or "error" => "failure",
            _ => "success"
        };

    private static string NormalizeLevel(string? value, string? status)
        => value?.Trim().ToLowerInvariant() switch
        {
            "info" => "info",
            "warning" or "warn" => "warning",
            "error" => "error",
            _ => NormalizeStatus(status) switch
            {
                "failure" => "error",
                "warning" => "warning",
                _ => "info"
            }
        };

    private static double? NormalizeDuration(double? value)
        => value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value)
            ? null
            : Math.Round(Math.Clamp(value.Value, 0, 600_000), 1);

    private static string SanitizeText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        text = BearerSecretRegex().Replace(text, "$1[redacted]");
        text = NamedSecretRegex().Replace(text, "$1=[redacted]");
        text = WhitespaceRegex().Replace(text, " ");
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }

    private static string? SanitizeProject(string? value)
    {
        var project = SanitizeText(value, 80);
        return string.IsNullOrWhiteSpace(project) ? null : project;
    }

    private static string? SanitizeIdentifier(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = IdentifierRegex().Replace(value.Trim(), string.Empty);
        if (cleaned.Length == 0) return null;
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static bool SecretEquals(string left, string right)
    {
        var x = Encoding.UTF8.GetBytes(left);
        var y = Encoding.UTF8.GetBytes(right);
        return x.Length == y.Length && CryptographicOperations.FixedTimeEquals(x, y);
    }

    [GeneratedRegex(@"(?i)(\bBearer\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerSecretRegex();

    [GeneratedRegex(@"(?i)\b(password|passwd|token|secret|api[-_ ]?key|credential)\s*[:=]\s*[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex NamedSecretRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[^A-Za-z0-9._:-]", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierRegex();
}

public sealed record AgentActivityReport(
    string AgentId,
    string Type,
    string? Status = null,
    string? Level = null,
    string? Operation = null,
    string? Message = null,
    double? DurationMs = null,
    string? RequestId = null,
    string? Project = null);

public sealed record AgentActivityEntry(
    string Category,
    string Type,
    string Status,
    string Level,
    string Operation,
    string Message,
    double? DurationMs,
    string? RequestId,
    DateTimeOffset CreatedAt,
    string? Project = null);

internal sealed record StoredAgentActivity(
    int Version,
    string Status,
    string Level,
    string Operation,
    string Message,
    double? DurationMs,
    string? RequestId,
    string? Project = null);