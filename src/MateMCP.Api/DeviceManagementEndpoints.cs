using System.Security.Cryptography;
using System.Text;
using MateMCP.Api.Data;
using Microsoft.EntityFrameworkCore;

public static class DeviceManagementEndpoints
{
    public static void Map(WebApplication app, string relayUrl)
    {
        app.MapGet("/api/agents/{agentId}/devices", async (string agentId, HttpContext context, ControlPlaneDbContext db) =>
        {
            var actor = await AuthenticateAgent(context, agentId, db);
            if (actor is null) return Results.Unauthorized();

            List<AgentDevice> devices;
            if (db.Database.IsSqlite())
            {
                // EF Core's SQLite provider rejects LINQ ORDER BY over DateTimeOffset,
                // even though SQLite can order the stored ISO-8601 value correctly.
                // Keep filtering and ordering in the database and bypass only the
                // provider translation limitation. Agent CreatedAt values are written
                // in UTC, so their persisted representation preserves chronology.
                devices = await db.Agents
                    .FromSqlInterpolated($"""
                        SELECT *
                        FROM "Agents"
                        WHERE "OwnerId" = {actor.OwnerId} AND "IsRevoked" = 0
                        ORDER BY "Name", "CreatedAt"
                        """)
                    .AsNoTracking()
                    .ToListAsync(context.RequestAborted);
            }
            else
            {
                devices = await db.Agents
                    .Where(x => x.OwnerId == actor.OwnerId && !x.IsRevoked)
                    .OrderBy(x => x.Name)
                    .ThenBy(x => x.CreatedAt)
                    .AsNoTracking()
                    .ToListAsync(context.RequestAborted);
            }

            var onlineAfter = DateTimeOffset.UtcNow.AddMinutes(-2);

            return Results.Ok(devices.Select(x => new
            {
                id = x.PublicId,
                x.Name,
                x.Platform,
                status = x.LastSeenAt > onlineAfter ? "online" : "offline",
                x.CreatedAt,
                x.LastSeenAt,
                mcpUrl = $"{relayUrl}/mcp/{x.PublicId}",
                isCurrent = string.Equals(x.PublicId, actor.PublicId, StringComparison.Ordinal)
            }));
        });

        app.MapDelete("/api/agents/{agentId}/devices/{targetAgentId}", async (
            string agentId,
            string targetAgentId,
            HttpContext context,
            ControlPlaneDbContext db) =>
        {
            var actor = await AuthenticateAgent(context, agentId, db);
            if (actor is null) return Results.Unauthorized();

            var target = await db.Agents.SingleOrDefaultAsync(
                x => x.PublicId == targetAgentId && x.OwnerId == actor.OwnerId && !x.IsRevoked,
                context.RequestAborted);
            if (target is null) return Results.NotFound();

            target.IsRevoked = true;
            db.AuditEvents.Add(new AuditEvent
            {
                UserId = actor.OwnerId,
                AgentDeviceId = target.Id,
                EventType = "agent.revoked.by-device",
                Detail = $"{target.Name}; actor={actor.PublicId}"
            });
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.Ok(new { status = "revoked", deviceId = target.PublicId });
        });
    }

    private static async Task<AgentDevice?> AuthenticateAgent(HttpContext context, string id, ControlPlaneDbContext db)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var credentialHash = Hash(authorization[7..]);
        return await db.Agents.SingleOrDefaultAsync(
            x => x.PublicId == id && x.CredentialHash == credentialHash && !x.IsRevoked,
            context.RequestAborted);
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
