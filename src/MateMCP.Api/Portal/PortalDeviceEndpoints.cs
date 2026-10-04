using System.Security.Claims;
using MateMCP.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MateMCP.Api.Portal;

public static class PortalDeviceEndpoints
{
    private const int ActivityPageSize = 50;

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
            ControlPlaneDbContext db,
            string? activityType = null,
            string? activityStatus = null,
            string? activityProject = null,
            string? q = null,
            int page = 1) =>
        {
            var ownerId = UserId(principal);
            var agent = await db.Agents
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    x => x.PublicId == agentId && x.OwnerId == ownerId,
                    context.RequestAborted);

            if (agent is null) return Results.NotFound();

            var recentAudit = await db.AuditEvents
                .Where(x => x.AgentDeviceId == agent.Id && (
                    x.EventType.StartsWith("runtime.") ||
                    x.EventType.StartsWith("agent.") ||
                    x.EventType.StartsWith("approval.") ||
                    x.EventType == "admin.agent.revoked"))
                .OrderByDescending(x => x.Id)
                .Take(500)
                .AsNoTracking()
                .ToListAsync(context.RequestAborted);

            var entries = recentAudit.Select(AgentActivityEndpoints.ToEntry).Where(x => x is not null).Cast<AgentActivityEntry>().ToList();
            var normalizedType = NormalizeActivityType(activityType);
            var normalizedStatus = NormalizeActivityStatus(activityStatus);
            var availableProjects = entries
                .Select(x => x.Project)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var requestedProject = string.IsNullOrWhiteSpace(activityProject) ? null : activityProject.Trim();
            if (requestedProject?.Length > 80) requestedProject = requestedProject[..80];
            var normalizedProject = requestedProject is null
                ? null
                : availableProjects.FirstOrDefault(x => string.Equals(x, requestedProject, StringComparison.OrdinalIgnoreCase));
            var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            if (search?.Length > 120) search = search[..120];
            var filteredEntries = entries.Where(x =>
                (normalizedType is null || x.Category == normalizedType) &&
                (normalizedStatus is null || x.Status == normalizedStatus) &&
                (normalizedProject is null || string.Equals(x.Project, normalizedProject, StringComparison.OrdinalIgnoreCase)) &&
                (search is null || x.Operation.Contains(search, StringComparison.OrdinalIgnoreCase) || x.Message.Contains(search, StringComparison.OrdinalIgnoreCase) || x.Type.Contains(search, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var totalFiltered = filteredEntries.Count;
            var totalPages = Math.Max(1, (totalFiltered + ActivityPageSize - 1) / ActivityPageSize);
            var currentPage = Math.Clamp(page, 1, totalPages);
            var pagedEntries = filteredEntries
                .Skip((currentPage - 1) * ActivityPageSize)
                .Take(ActivityPageSize)
                .ToList();

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
                      <div><dt>Last activity</dt><dd>{PortalUi.H(entries.Count == 0 ? "No activity yet" : FormatLastSeen(entries[0].CreatedAt))}</dd></div>
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
                <section class="portal-section activity-panel" id="activity">
                  <div class="portal-section-header activity-header">
                    <div><h2>Recent activity</h2><p>Newest first · up to 500 recent Agent events · 50 per page. Command arguments and secret values are not stored here.</p></div>
                    <span class="section-count">{filteredEntries.Count}</span>
                  </div>
                  <form class="activity-filters" method="get" action="/devices/{PortalUi.H(agent.PublicId)}#activity">
                    <label><span>Type</span><select name="activityType">{ActivityOption("all", "All activity", normalizedType)}{ActivityOption("connection", "Connection", normalizedType)}{ActivityOption("request", "MCP requests", normalizedType)}{ActivityOption("security", "Security", normalizedType)}{ActivityOption("approval", "Approvals", normalizedType)}</select></label>
                    <label><span>Status</span><select name="activityStatus">{ActivityOption("all", "All statuses", normalizedStatus)}{ActivityOption("success", "Success", normalizedStatus)}{ActivityOption("warning", "Warning", normalizedStatus)}{ActivityOption("failure", "Failure", normalizedStatus)}</select></label>
                    <label class="activity-project-filter"><span>Project</span><select name="activityProject">{ActivityProjectOptions(availableProjects, normalizedProject)}</select></label>
                    <label class="activity-search"><span>Search</span><input name="q" value="{PortalUi.H(search ?? string.Empty)}" placeholder="Tool, event, or message" autocomplete="off"></label>
                    <button class="button button-secondary button-compact" type="submit">Filter</button>
                    {(normalizedType is not null || normalizedStatus is not null || normalizedProject is not null || search is not null ? $"""<a class="button button-ghost button-compact" href="/devices/{PortalUi.H(agent.PublicId)}#activity">Clear</a>""" : "")}
                  </form>
                  {(filteredEntries.Count == 0 ? PortalUi.EmptyState("No matching activity", entries.Count == 0 ? "No activity has been recorded for this Agent yet." : "Try changing or clearing the activity filters.") : $"""<div class="activity-list">{string.Join("", pagedEntries.Select(ActivityRow))}</div>""")}
                  {ActivityPagination(agent.PublicId, currentPage, totalPages, totalFiltered, normalizedType, normalizedStatus, normalizedProject, search)}
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

    private static string? NormalizeActivityType(string? value)
        => value?.Trim().ToLowerInvariant() switch { "connection" => "connection", "request" => "request", "security" => "security", "approval" => "approval", _ => null };

    private static string? NormalizeActivityStatus(string? value)
        => value?.Trim().ToLowerInvariant() switch { "success" => "success", "warning" => "warning", "failure" => "failure", _ => null };

    private static string ActivityOption(string value, string label, string? selected)
    {
        var normalizedValue = value == "all" ? null : value;
        var isSelected = string.Equals(normalizedValue, selected, StringComparison.Ordinal);
        return $"""<option value="{PortalUi.H(value)}"{(isSelected ? " selected" : "")}>{PortalUi.H(label)}</option>""";
    }

    private static string ActivityProjectOptions(IEnumerable<string> projects, string? selected)
    {
        var options = new List<string>
        {
            $"""<option value="all"{(selected is null ? " selected" : "")}>All projects</option>"""
        };
        options.AddRange(projects.Select(project =>
            $"""<option value="{PortalUi.H(project)}"{(string.Equals(project, selected, StringComparison.OrdinalIgnoreCase) ? " selected" : "")}>{PortalUi.H(project)}</option>"""));
        return string.Join("", options);
    }

    private static string ActivityPagination(
        string agentId,
        int currentPage,
        int totalPages,
        int totalItems,
        string? activityType,
        string? activityStatus,
        string? activityProject,
        string? search)
    {
        if (totalItems == 0) return string.Empty;

        var firstItem = ((currentPage - 1) * ActivityPageSize) + 1;
        var lastItem = Math.Min(currentPage * ActivityPageSize, totalItems);
        var previous = currentPage > 1
            ? $"""<a class="button button-ghost button-compact" href="{PortalUi.H(ActivityPageUrl(agentId, currentPage - 1, activityType, activityStatus, activityProject, search))}">Previous</a>"""
            : "";
        var next = currentPage < totalPages
            ? $"""<a class="button button-ghost button-compact" href="{PortalUi.H(ActivityPageUrl(agentId, currentPage + 1, activityType, activityStatus, activityProject, search))}">Next</a>"""
            : "";

        return $"""
            <nav class="activity-pagination" aria-label="Activity pages">
              <span class="activity-page-summary">Showing {firstItem}–{lastItem} of {totalItems} · Page {currentPage} of {totalPages}</span>
              <span class="activity-page-actions">{previous}{next}</span>
            </nav>
            """;
    }

    private static string ActivityPageUrl(
        string agentId,
        int page,
        string? activityType,
        string? activityStatus,
        string? activityProject,
        string? search)
    {
        var query = new List<string> { $"page={Math.Max(1, page)}" };
        if (activityType is not null) query.Add($"activityType={Uri.EscapeDataString(activityType)}");
        if (activityStatus is not null) query.Add($"activityStatus={Uri.EscapeDataString(activityStatus)}");
        if (activityProject is not null) query.Add($"activityProject={Uri.EscapeDataString(activityProject)}");
        if (search is not null) query.Add($"q={Uri.EscapeDataString(search)}");
        return $"/devices/{Uri.EscapeDataString(agentId)}?{string.Join("&", query)}#activity";
    }

    private static string ActivityRow(AgentActivityEntry entry)
    {
        var icon = entry.Category switch { "connection" => "↔", "request" => "›_", "security" => "◇", "approval" => "✓", _ => "·" };
        var details = string.IsNullOrWhiteSpace(entry.Message) ? "" : $"""<p>{PortalUi.H(entry.Message)}</p>""";
        var duration = entry.DurationMs is null ? "" : $"""<span>{PortalUi.H(FormatDuration(entry.DurationMs.Value))}</span>""";
        var requestId = string.IsNullOrWhiteSpace(entry.RequestId) ? "" : $"""<span class="activity-request-id" title="{PortalUi.H(entry.RequestId)}">req {PortalUi.H(ShortId(entry.RequestId))}</span>""";
        var project = string.IsNullOrWhiteSpace(entry.Project) ? "" : $"""<span class="activity-project-meta">Project {PortalUi.H(entry.Project)}</span>""";
        return $"""
            <article class="activity-row activity-{PortalUi.H(entry.Level)}">
              <div class="activity-icon" aria-hidden="true">{PortalUi.H(icon)}</div>
              <div class="activity-body">
                <div class="activity-title"><strong>{PortalUi.H(entry.Operation)}</strong>{PortalUi.StatusBadge(entry.Status)}</div>
                {details}
                <div class="activity-meta"><time datetime="{entry.CreatedAt:O}" title="{PortalUi.H(entry.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'"))}">{PortalUi.H(FormatLastSeen(entry.CreatedAt))}</time><span>{PortalUi.H(entry.Category)}</span>{project}{duration}{requestId}</div>
              </div>
            </article>
            """;
    }

    private static string FormatDuration(double milliseconds) => milliseconds < 1000 ? $"{milliseconds:0} ms" : $"{milliseconds / 1000:0.0} s";
    private static string ShortId(string value) => value.Length <= 12 ? value : value[..12];

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