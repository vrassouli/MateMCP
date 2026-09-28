using System.Security.Claims;
using MateMCP.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MateMCP.Api.Portal;

public static class PortalDeviceEndpoints
{
    public static void Map(WebApplication app, string relayUrl)
    {
        var group = app.MapGroup("/devices").RequireAuthorization();

        group.MapGet("", async (
            HttpContext context,
            ClaimsPrincipal principal,
            ControlPlaneDbContext db,
            bool showRevoked = false,
            string? notice = null) =>
        {
            var ownerId = UserId(principal);
            var active = await db.Agents
                .Where(x => x.OwnerId == ownerId && !x.IsRevoked)
                .OrderBy(x => x.Name)
                .AsNoTracking()
                .ToListAsync(context.RequestAborted);

            var revokedCount = await db.Agents.CountAsync(
                x => x.OwnerId == ownerId && x.IsRevoked,
                context.RequestAborted);

            var revoked = showRevoked
                ? await db.Agents
                    .Where(x => x.OwnerId == ownerId && x.IsRevoked)
                    .OrderBy(x => x.Name)
                    .AsNoTracking()
                    .ToListAsync(context.RequestAborted)
                : [];

            var headingActions = $"""
                <a class="button button-secondary" href="/device">Add device</a>
                {(revokedCount == 0
                    ? ""
                    : showRevoked
                        ? """<a class="button button-ghost" href="/devices">Hide revoked history</a>"""
                        : $"""<a class="button button-ghost" href="/devices?showRevoked=true">Revoked history <span class="button-count">{revokedCount}</span></a>""")}
                """;

            var body = $"""
                {PortalUi.PageHeading(
                    "Device management",
                    "Devices",
                    "Manage the computers connected to your MateMCP account. Revoked devices stay out of the active working list.",
                    headingActions)}
                {(notice == "revoked" ? PortalUi.Alert("Device access was revoked. It has been removed from your active device list.", "success") : "")}
                <section class="device-overview" aria-label="Device summary">
                  <div><strong>{active.Count}</strong><span>Active devices</span></div>
                  <div><strong>{active.Count(IsOnline)}</strong><span>Online now</span></div>
                  <div><strong>{revokedCount}</strong><span>Revoked history</span></div>
                </section>
                <section class="portal-section">
                  <div class="portal-section-header">
                    <div><h2>Active devices</h2><p>Only devices that can still authenticate are shown here.</p></div>
                    <span class="section-count">{active.Count}</span>
                  </div>
                  {(active.Count == 0
                      ? PortalUi.EmptyState(
                          "No active devices",
                          revokedCount > 0
                              ? "All previously enrolled devices are revoked. Add a device to reconnect a machine."
                              : "Enroll MateMCP Desktop on a computer to see it here.",
                          """<a class="button button-secondary" href="/device">Add a device</a>""")
                      : $"""<div class="device-card-grid">{string.Join("", active.Select(x => DeviceCard(x, relayUrl)))}</div>""")}
                </section>
                {(showRevoked
                    ? $"""
                      <section class="portal-section revoked-history">
                        <div class="portal-section-header">
                          <div><h2>Revoked history</h2><p>Kept separately for security history. These devices can no longer authenticate.</p></div>
                          <span class="section-count">{revoked.Count}</span>
                        </div>
                        {(revoked.Count == 0
                            ? PortalUi.EmptyState("No revoked devices", "There is no revoked device history for this account.")
                            : $"""<div class="revoked-device-list">{string.Join("", revoked.Select(RevokedRow))}</div>""")}
                      </section>
                      """
                    : "")}
                """;

            return PortalUi.AppPage(
                context,
                "Devices",
                principal.Identity?.Name ?? "MateMCP user",
                body,
                "devices");
        });

        group.MapGet("/{agentId}", async (
            string agentId,
            HttpContext context,
            ClaimsPrincipal principal,
            ControlPlaneDbContext db) =>
        {
            var ownerId = UserId(principal);
            var agent = await db.Agents
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.PublicId == agentId && x.OwnerId == ownerId,
                    context.RequestAborted);

            if (agent is null) return Results.NotFound();

            var status = agent.IsRevoked ? "revoked" : IsOnline(agent) ? "online" : "offline";
            var action = agent.IsRevoked
                ? """<a class="button button-secondary" href="/devices?showRevoked=true">Back to revoked history</a>"""
                : $"""<a class="button button-danger" href="/devices/{PortalUi.H(agent.PublicId)}/revoke">Revoke access</a>""";

            var body = $"""
                {PortalUi.PageHeading(
                    "Device management",
                    agent.Name,
                    agent.IsRevoked
                        ? "This device has been revoked and cannot authenticate with MateMCP."
                        : "Review connection details and manage access for this device.",
                    $"""<a class="button button-ghost" href="/devices">Back to devices</a>{action}""")}
                {(agent.IsRevoked
                    ? PortalUi.Alert("This device is revoked. It is retained only in device history and audit records.", "warning")
                    : "")}
                <section class="device-detail-layout">
                  <article class="portal-section device-detail-card">
                    <div class="portal-section-header"><div><h2>Device details</h2><p>Connection and enrollment information.</p></div>{PortalUi.StatusBadge(status)}</div>
                    <dl class="detail-list">
                      <div><dt>Device name</dt><dd>{PortalUi.H(agent.Name)}</dd></div>
                      <div><dt>Platform</dt><dd>{PortalUi.H(agent.Platform)}</dd></div>
                      <div><dt>Device ID</dt><dd><code>{PortalUi.H(agent.PublicId)}</code></dd></div>
                      <div><dt>Enrolled</dt><dd>{PortalUi.H(FormatDate(agent.CreatedAt))}</dd></div>
                      <div><dt>Last seen</dt><dd>{PortalUi.H(FormatLastSeen(agent.LastSeenAt))}</dd></div>
                    </dl>
                  </article>
                  <article class="portal-section device-endpoint-card">
                    <div class="portal-section-header"><div><h2>MCP endpoint</h2><p>Use this endpoint when connecting a supported MCP client.</p></div></div>
                    <div class="endpoint-panel">
                      <code>{PortalUi.H($"{relayUrl}/mcp/{agent.PublicId}")}</code>
                      <button class="button button-secondary button-compact" type="button" data-copy-value="{PortalUi.H($"{relayUrl}/mcp/{agent.PublicId}")}">Copy</button>
                    </div>
                    <p class="endpoint-note">The endpoint is safe to display. Device credentials and secret material are never rendered in this portal.</p>
                  </article>
                </section>
                """;

            return PortalUi.AppPage(
                context,
                agent.Name,
                principal.Identity?.Name ?? "MateMCP user",
                body,
                "devices");
        });

        group.MapGet("/{agentId}/revoke", async (
            string agentId,
            HttpContext context,
            ClaimsPrincipal principal,
            ControlPlaneDbContext db) =>
        {
            var ownerId = UserId(principal);
            var agent = await db.Agents
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.PublicId == agentId && x.OwnerId == ownerId && !x.IsRevoked,
                    context.RequestAborted);

            if (agent is null) return Results.NotFound();

            var body = $"""
                {PortalUi.PageHeading(
                    "Device management",
                    "Revoke device access",
                    "This security action takes effect immediately.")}
                <section class="danger-confirm-card" aria-labelledby="revoke-title">
                  <div class="danger-confirm-icon" aria-hidden="true">!</div>
                  <div>
                    <h2 id="revoke-title">Revoke {PortalUi.H(agent.Name)}?</h2>
                    <p>This device will immediately lose access to MateMCP and disappear from your active device list. Its security history will remain available under revoked history.</p>
                    <dl class="confirm-device-meta">
                      <div><dt>Device</dt><dd>{PortalUi.H(agent.Name)}</dd></div>
                      <div><dt>Platform</dt><dd>{PortalUi.H(agent.Platform)}</dd></div>
                      <div><dt>Device ID</dt><dd><code>{PortalUi.H(agent.PublicId)}</code></dd></div>
                    </dl>
                    <div class="danger-confirm-actions">
                      <a class="button button-secondary" href="/devices/{PortalUi.H(agent.PublicId)}">Cancel</a>
                      <form method="post" action="/devices/{PortalUi.H(agent.PublicId)}/revoke">
                        <button class="button button-danger" type="submit">Revoke access</button>
                      </form>
                    </div>
                  </div>
                </section>
                """;

            return PortalUi.AppPage(
                context,
                "Confirm device revoke",
                principal.Identity?.Name ?? "MateMCP user",
                body,
                "devices");
        });

        group.MapPost("/{agentId}/revoke", async (
            string agentId,
            HttpContext context,
            ClaimsPrincipal principal,
            ControlPlaneDbContext db) =>
        {
            var ownerId = UserId(principal);
            var agent = await db.Agents.SingleOrDefaultAsync(
                x => x.PublicId == agentId && x.OwnerId == ownerId && !x.IsRevoked,
                context.RequestAborted);

            if (agent is null) return Results.NotFound();

            agent.IsRevoked = true;
            db.AuditEvents.Add(new AuditEvent
            {
                UserId = ownerId,
                AgentDeviceId = agent.Id,
                EventType = "agent.revoked",
                Detail = agent.Name
            });
            await db.SaveChangesAsync(context.RequestAborted);

            return Results.Redirect("/devices?notice=revoked");
        });
    }

    private static string DeviceCard(AgentDevice agent, string relayUrl)
    {
        var status = IsOnline(agent) ? "online" : "offline";
        var endpoint = $"{relayUrl}/mcp/{agent.PublicId}";

        return $"""
            <article class="device-card">
              <div class="device-card-top">
                <div class="device-card-identity">
                  <span class="device-card-icon" aria-hidden="true">{PortalUi.H(PlatformInitial(agent.Platform))}</span>
                  <div><h3>{PortalUi.H(agent.Name)}</h3><p>{PortalUi.H(agent.Platform)}</p></div>
                </div>
                {PortalUi.StatusBadge(status)}
              </div>
              <dl class="device-card-facts">
                <div><dt>Last seen</dt><dd>{PortalUi.H(FormatLastSeen(agent.LastSeenAt))}</dd></div>
                <div><dt>Enrolled</dt><dd>{PortalUi.H(FormatDate(agent.CreatedAt))}</dd></div>
              </dl>
              <div class="device-card-endpoint">
                <code title="{PortalUi.H(endpoint)}">{PortalUi.H(endpoint)}</code>
                <button class="copy-icon-button" type="button" data-copy-value="{PortalUi.H(endpoint)}" aria-label="Copy MCP endpoint">⧉</button>
              </div>
              <div class="device-card-footer">
                <span class="device-public-id">{PortalUi.H(agent.PublicId)}</span>
                <a class="button button-secondary button-compact" href="/devices/{PortalUi.H(agent.PublicId)}">Manage</a>
              </div>
            </article>
            """;
    }

    private static string RevokedRow(AgentDevice agent)
        => $"""
           <article class="revoked-device-row">
             <div class="device-name">
               <span class="device-icon" aria-hidden="true">{PortalUi.H(PlatformInitial(agent.Platform))}</span>
               <div><strong>{PortalUi.H(agent.Name)}</strong><span>{PortalUi.H(agent.Platform)} · {PortalUi.H(agent.PublicId)}</span></div>
             </div>
             {PortalUi.StatusBadge("revoked")}
             <a class="button button-ghost button-compact" href="/devices/{PortalUi.H(agent.PublicId)}">View history</a>
           </article>
           """;

    private static bool IsOnline(AgentDevice agent)
        => !agent.IsRevoked &&
           agent.LastSeenAt is not null &&
           agent.LastSeenAt > DateTimeOffset.UtcNow.AddMinutes(-2);

    private static string PlatformInitial(string platform)
        => string.IsNullOrWhiteSpace(platform) ? "D" : platform.Trim()[..1].ToUpperInvariant();

    private static string FormatDate(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'");

    private static string FormatLastSeen(DateTimeOffset? value)
    {
        if (value is null) return "Never";

        var elapsed = DateTimeOffset.UtcNow - value.Value;
        if (elapsed < TimeSpan.Zero) return "Just now";
        if (elapsed < TimeSpan.FromMinutes(1)) return "Just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)elapsed.TotalMinutes)} min ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)elapsed.TotalHours)} hr ago";
        return value.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'");
    }

    private static Guid UserId(ClaimsPrincipal principal)
        => Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
}
