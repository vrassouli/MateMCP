using System.Security.Claims;
using MateMCP.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MateMCP.Api.Portal;

public static class PortalApprovalEndpoints
{
    private static readonly HashSet<string> HistoryFilters =
        new(["all", "allowed", "denied", "expired"], StringComparer.Ordinal);

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/approvals").RequireAuthorization();

        group.MapGet("", async (
            HttpContext context,
            ClaimsPrincipal principal,
            ControlPlaneDbContext db,
            string filter = "all",
            string? notice = null) =>
        {
            var ownerId = UserId(principal);
            var now = DateTimeOffset.UtcNow;
            filter = NormalizeFilter(filter);

            await ExpireOwnedPendingAsync(ownerId, now, db, context.RequestAborted);

            var pending = (await db.Approvals
                .Include(x => x.AgentDevice)
                .Where(x =>
                    x.AgentDevice!.OwnerId == ownerId &&
                    x.Status == "pending")
                .AsNoTracking()
                .ToListAsync(context.RequestAborted))
                .Where(x => x.ExpiresAt > now)
                .OrderBy(x => x.ExpiresAt)
                .ToList();

            var historyQuery = db.Approvals
                .Include(x => x.AgentDevice)
                .Where(x =>
                    x.AgentDevice!.OwnerId == ownerId &&
                    x.Status != "pending");

            if (filter != "all")
                historyQuery = historyQuery.Where(x => x.Status == filter);

            var history = (await historyQuery
                .AsNoTracking()
                .ToListAsync(context.RequestAborted))
                .OrderByDescending(x => x.DecidedAt ?? x.ExpiresAt)
                .ThenByDescending(x => x.CreatedAt)
                .Take(50)
                .ToList();

            var allowedCount = await db.Approvals.CountAsync(
                x => x.AgentDevice!.OwnerId == ownerId && x.Status == "allowed",
                context.RequestAborted);
            var deniedCount = await db.Approvals.CountAsync(
                x => x.AgentDevice!.OwnerId == ownerId && x.Status == "denied",
                context.RequestAborted);
            var expiredCount = await db.Approvals.CountAsync(
                x => x.AgentDevice!.OwnerId == ownerId && x.Status == "expired",
                context.RequestAborted);

            var noticeMarkup = notice switch
            {
                "allowed" => PortalUi.Alert("Approval allowed. The requesting Agent can continue while the request remains valid.", "success"),
                "denied" => PortalUi.Alert("Approval denied. The requesting Agent will receive the denied decision.", "success"),
                "expired" => PortalUi.Alert("That approval expired before a decision could be recorded.", "warning"),
                "already-decided" => PortalUi.Alert("That approval was already decided and no longer needs action.", "warning"),
                _ => ""
            };

            var pendingContent = pending.Count == 0
                ? PortalUi.EmptyState(
                    "Inbox clear",
                    "There are no approval requests waiting for your decision.")
                : $"""
                  <div class="approval-inbox-list">
                    {string.Join("", pending.Select(x => PendingCard(x, now)))}
                  </div>
                  """;

            var historyContent = history.Count == 0
                ? PortalUi.EmptyState(
                    "No matching history",
                    filter == "all"
                        ? "Allowed, denied, and expired approval requests will appear here."
                        : $"There are no {filter} approval requests in recent history.")
                : $"""
                  <div class="approval-history-list">
                    {string.Join("", history.Select(HistoryRow))}
                  </div>
                  """;

            var filters = string.Join("", new[]
            {
                FilterLink("all", "All", filter),
                FilterLink("allowed", "Allowed", filter),
                FilterLink("denied", "Denied", filter),
                FilterLink("expired", "Expired", filter)
            });

            var body = $"""
                {PortalUi.PageHeading(
                    "Human approvals",
                    "Approvals",
                    "Review sensitive Agent actions, make a decision, and keep a concise audit-friendly history.")}
                {noticeMarkup}
                <section class="approval-overview" aria-label="Approval summary">
                  <div><strong>{pending.Count}</strong><span>Waiting now</span></div>
                  <div><strong>{allowedCount}</strong><span>Allowed</span></div>
                  <div><strong>{deniedCount}</strong><span>Denied</span></div>
                  <div><strong>{expiredCount}</strong><span>Expired</span></div>
                </section>
                <section class="portal-section approval-inbox">
                  <div class="portal-section-header">
                    <div><h2>Pending inbox</h2><p>Requests are ordered by the nearest expiry time.</p></div>
                    <span class="section-count">{pending.Count}</span>
                  </div>
                  {pendingContent}
                </section>
                <section class="portal-section approval-history">
                  <div class="portal-section-header approval-history-header">
                    <div><h2>Recent history</h2><p>Up to the 50 most recent completed or expired requests.</p></div>
                    <nav class="approval-filter" aria-label="Approval history filter">{filters}</nav>
                  </div>
                  {historyContent}
                </section>
                """;

            return PortalUi.AppPage(
                context,
                "Approvals",
                principal.Identity?.Name ?? "MateMCP user",
                body,
                "approvals");
        });

        group.MapPost("/{id:guid}/{decision}", async (
            Guid id,
            string decision,
            HttpContext context,
            ClaimsPrincipal principal,
            ControlPlaneDbContext db) =>
        {
            if (decision is not ("allow" or "deny"))
                return Results.NotFound();

            var ownerId = UserId(principal);
            var approval = await db.Approvals
                .Include(x => x.AgentDevice)
                .SingleOrDefaultAsync(
                    x => x.Id == id && x.AgentDevice!.OwnerId == ownerId,
                    context.RequestAborted);

            if (approval is null)
                return Results.NotFound();

            if (approval.Status != "pending")
                return Results.Redirect("/approvals?notice=already-decided");

            if (approval.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                approval.Status = "expired";
                await db.SaveChangesAsync(context.RequestAborted);
                return Results.Redirect("/approvals?notice=expired");
            }

            approval.Status = decision == "allow" ? "allowed" : "denied";
            approval.DecidedAt = DateTimeOffset.UtcNow;
            db.AuditEvents.Add(new AuditEvent
            {
                UserId = ownerId,
                AgentDeviceId = approval.AgentDeviceId,
                EventType = "approval." + approval.Status,
                Detail = approval.OperationHash
            });

            await db.SaveChangesAsync(context.RequestAborted);
            return Results.Redirect($"/approvals?notice={approval.Status}");
        });
    }

    public static async Task ExpireOwnedPendingAsync(
        Guid ownerId,
        DateTimeOffset now,
        ControlPlaneDbContext db,
        CancellationToken cancellationToken = default)
    {
        var expired = (await db.Approvals
            .Include(x => x.AgentDevice)
            .Where(x =>
                x.AgentDevice!.OwnerId == ownerId &&
                x.Status == "pending")
            .ToListAsync(cancellationToken))
            .Where(x => x.ExpiresAt <= now)
            .ToList();

        if (expired.Count == 0)
            return;

        foreach (var approval in expired)
            approval.Status = "expired";

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string PendingCard(ApprovalRequest approval, DateTimeOffset now)
    {
        var remaining = approval.ExpiresAt - now;
        return $"""
            <article class="approval-inbox-card">
              <div class="approval-inbox-head">
                <div class="approval-capability">
                  <span class="approval-capability-icon" aria-hidden="true">!</span>
                  <div>
                    <div class="approval-meta-line">
                      <strong>{PortalUi.H(approval.Capability)}</strong>
                      {PortalUi.StatusBadge("pending")}
                    </div>
                    <span class="approval-context">Requested by <a href="/devices/{PortalUi.H(approval.AgentDevice!.PublicId)}">{PortalUi.H(approval.AgentDevice.Name)}</a> · expires {PortalUi.H(FormatRemaining(remaining))}</span>
                  </div>
                </div>
                <time datetime="{approval.ExpiresAt:O}" class="approval-expiry">{PortalUi.H(FormatTime(approval.ExpiresAt))}</time>
              </div>
              <div class="approval-detail-block">
                <span>Target</span>
                <code>{PortalUi.H(approval.Target)}</code>
              </div>
              <div class="approval-summary-block">
                <span>Why this action is requested</span>
                <p>{PortalUi.H(approval.Summary)}</p>
              </div>
              <div class="approval-inbox-footer">
                <span class="approval-request-id">Request {PortalUi.H(ShortId(approval.Id))}</span>
                <div class="approval-decision-actions">
                  <form method="post" action="/approvals/{approval.Id}/deny">
                    <button class="button button-danger" type="submit">Deny</button>
                  </form>
                  <form method="post" action="/approvals/{approval.Id}/allow">
                    <button class="button button-primary" type="submit">Allow once</button>
                  </form>
                </div>
              </div>
            </article>
            """;
    }

    private static string HistoryRow(ApprovalRequest approval)
    {
        var eventTime = approval.DecidedAt ?? approval.ExpiresAt;
        return $"""
            <article class="approval-history-row">
              <div class="approval-history-main">
                <div class="approval-meta-line">
                  <strong>{PortalUi.H(approval.Capability)}</strong>
                  {PortalUi.StatusBadge(approval.Status)}
                </div>
                <div class="approval-history-context">
                  <a href="/devices/{PortalUi.H(approval.AgentDevice!.PublicId)}">{PortalUi.H(approval.AgentDevice.Name)}</a>
                  <span>·</span>
                  <time datetime="{eventTime:O}">{PortalUi.H(FormatTime(eventTime))}</time>
                  <span>·</span>
                  <span>{PortalUi.H(ShortId(approval.Id))}</span>
                </div>
                <details class="approval-history-details">
                  <summary>View request details</summary>
                  <div class="approval-detail-block">
                    <span>Target</span>
                    <code>{PortalUi.H(approval.Target)}</code>
                  </div>
                  <div class="approval-summary-block">
                    <span>Summary</span>
                    <p>{PortalUi.H(approval.Summary)}</p>
                  </div>
                </details>
              </div>
            </article>
            """;
    }

    private static string FilterLink(string value, string label, string current)
    {
        var activeClass = value == current ? " is-active" : "";
        var currentAttribute = value == current ? " aria-current=\"page\"" : "";
        return $"<a class=\"approval-filter-link{activeClass}\" href=\"/approvals?filter={value}\"{currentAttribute}>{label}</a>";
    }

    private static string NormalizeFilter(string filter)
    {
        filter = (filter ?? "all").Trim().ToLowerInvariant();
        return HistoryFilters.Contains(filter) ? filter : "all";
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero) return "now";
        if (remaining < TimeSpan.FromMinutes(1)) return $"in {Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))} sec";
        return $"in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} min";
    }

    private static string FormatTime(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

    private static string ShortId(Guid id)
        => id.ToString("N")[..8];

    private static Guid UserId(ClaimsPrincipal principal)
        => Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
}
