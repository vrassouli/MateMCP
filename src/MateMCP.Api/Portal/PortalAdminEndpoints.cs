using System.Security.Claims;
using MateMCP.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MateMCP.Api.Portal;

public static class PortalAdminEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/admin").RequireAuthorization("admin");

        group.MapGet("", async (
            HttpContext context,
            ClaimsPrincipal principal,
            ControlPlaneDbContext db,
            string? q = null,
            string? notice = null) =>
        {
            var query = (q ?? string.Empty).Trim();
            var normalized = query.ToUpperInvariant();
            var usersQuery = db.Users.Include(x => x.Agents).AsNoTracking().AsQueryable();

            if (normalized.Length > 0)
                usersQuery = usersQuery.Where(x => x.NormalizedEmail.Contains(normalized));

            var users = await usersQuery
                .OrderBy(x => x.NormalizedEmail)
                .Take(100)
                .ToListAsync(context.RequestAborted);

            var totalUsers = await db.Users.CountAsync(context.RequestAborted);
            var disabledUsers = await db.Users.CountAsync(x => x.IsDisabled, context.RequestAborted);
            var adminUsers = await db.Users.CountAsync(x => x.IsAdmin && !x.IsDisabled, context.RequestAborted);
            var activeDevices = await db.Agents.CountAsync(x => !x.IsRevoked, context.RequestAborted);

            var rows = users.Count == 0
                ? PortalUi.EmptyState("No matching users", query.Length == 0 ? "No user accounts exist yet." : "Try a different email search.")
                : $"""<div class="admin-user-list">{string.Join("", users.Select(UserRow))}</div>""";

            var noticeMarkup = notice switch
            {
                "enabled" => PortalUi.Alert("User account enabled. New sign-ins and Agent authorization are allowed again.", "success"),
                "disabled" => PortalUi.Alert("User account disabled. Existing web sessions and Agent authorization are now rejected.", "success"),
                "device-revoked" => PortalUi.Alert("Device access revoked for the selected user.", "success"),
                _ => ""
            };

            var body = $"""
                {PortalUi.PageHeading(
                    "Administration",
                    "Users",
                    "Manage account access and inspect enrolled devices. Administrative actions are enforced server-side.",
                    """<a class="button button-ghost" href="/dashboard">Back to control plane</a>""")}
                {noticeMarkup}
                <section class="admin-overview" aria-label="Administration summary">
                  <div><strong>{totalUsers}</strong><span>Total users</span></div>
                  <div><strong>{totalUsers - disabledUsers}</strong><span>Enabled</span></div>
                  <div><strong>{disabledUsers}</strong><span>Disabled</span></div>
                  <div><strong>{activeDevices}</strong><span>Active devices</span></div>
                </section>
                <section class="portal-section">
                  <div class="portal-section-header admin-users-header">
                    <div><h2>User accounts</h2><p>{adminUsers} enabled administrator{(adminUsers == 1 ? "" : "s")} · showing up to 100 accounts</p></div>
                    <form class="admin-search" method="get" action="/admin" role="search">
                      <label class="sr-only" for="admin-user-search">Search users by email</label>
                      <input id="admin-user-search" name="q" type="search" value="{PortalUi.H(query)}" placeholder="Search by email" autocomplete="off">
                      <button class="button button-secondary button-compact" type="submit">Search</button>
                      {(query.Length > 0 ? """<a class="button button-ghost button-compact" href="/admin">Clear</a>""" : "")}
                    </form>
                  </div>
                  {rows}
                </section>
                """;

            return PortalUi.AppPage(context, "Administration", principal.Identity?.Name ?? "MateMCP administrator", body, "admin");
        });

        group.MapGet("/users/{userId:guid}", async (
            Guid userId,
            HttpContext context,
            ClaimsPrincipal principal,
            ControlPlaneDbContext db,
            string? notice = null) =>
        {
            var user = await db.Users
                .Include(x => x.Agents)
                .AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == userId, context.RequestAborted);

            if (user is null) return Results.NotFound();

            var currentAdminId = CurrentUserId(principal);
            var activeDevices = user.Agents.Where(x => !x.IsRevoked).OrderBy(x => x.Name).ToList();
            var revokedDevices = user.Agents.Where(x => x.IsRevoked).OrderBy(x => x.Name).ToList();
            var pendingApprovals = await db.Approvals
                .Include(x => x.AgentDevice)
                .CountAsync(x => x.AgentDevice!.OwnerId == user.Id && x.Status == "pending", context.RequestAborted);

            var accountAction = user.Id == currentAdminId
                ? """<span class="admin-self-note">Current administrator</span>"""
                : user.IsDisabled
                    ? $"""<form method="post" action="/admin/users/{user.Id}/enable"><button class="button button-secondary" type="submit">Enable account</button></form>"""
                    : $"""<a class="button button-danger" href="/admin/users/{user.Id}/disable">Disable account</a>""";

            var deviceRows = user.Agents.Count == 0
                ? PortalUi.EmptyState("No devices", "This user has not enrolled any devices.")
                : $"""<div class="admin-device-list">{string.Join("", activeDevices.Select(AdminDeviceRow))}{string.Join("", revokedDevices.Select(AdminDeviceRow))}</div>""";

            var noticeMarkup = notice switch
            {
                "enabled" => PortalUi.Alert("Account enabled.", "success"),
                "disabled" => PortalUi.Alert("Account disabled.", "success"),
                "device-revoked" => PortalUi.Alert("Device access revoked.", "success"),
                _ => ""
            };

            var body = $"""
                {PortalUi.PageHeading(
                    "Administration",
                    user.Email,
                    "Review non-secret account state and device access.",
                    $"""<a class="button button-ghost" href="/admin">Back to users</a>{accountAction}""")}
                {noticeMarkup}
                <section class="admin-account-grid">
                  <article class="portal-section">
                    <div class="portal-section-header"><div><h2>Account</h2><p>Identity and access state.</p></div>{PortalUi.StatusBadge(user.IsDisabled ? "disabled" : "active")}</div>
                    <dl class="detail-list">
                      <div><dt>Email</dt><dd>{PortalUi.H(user.Email)}</dd></div>
                      <div><dt>Role</dt><dd><span class="role-badge{(user.IsAdmin ? " role-admin" : "")}">{(user.IsAdmin ? "Administrator" : "User")}</span></dd></div>
                      <div><dt>User ID</dt><dd><code>{user.Id}</code></dd></div>
                      <div><dt>Created</dt><dd>{PortalUi.H(FormatDate(user.CreatedAt))}</dd></div>
                      <div><dt>Active devices</dt><dd>{activeDevices.Count}</dd></div>
                      <div><dt>Pending approvals</dt><dd>{pendingApprovals}</dd></div>
                    </dl>
                  </article>
                  <article class="portal-section admin-security-card">
                    <div class="portal-section-header"><div><h2>Access behavior</h2><p>What account status controls.</p></div></div>
                    <div class="admin-security-copy">
                      <p>When an account is disabled, existing web sessions are rejected and its Agents cannot authenticate, request approvals, or pass internal authorization checks.</p>
                      <p>Device records remain enrolled so access can be restored if the account is enabled again. Revoking a device is separate and permanent for that device credential.</p>
                    </div>
                  </article>
                </section>
                <section class="portal-section admin-user-devices">
                  <div class="portal-section-header"><div><h2>Devices</h2><p>Active and revoked devices owned by this user.</p></div><span class="section-count">{user.Agents.Count}</span></div>
                  {deviceRows}
                </section>
                """;

            return PortalUi.AppPage(context, user.Email, principal.Identity?.Name ?? "MateMCP administrator", body, "admin");
        });

        group.MapGet("/users/{userId:guid}/disable", async (
            Guid userId,
            HttpContext context,
            ClaimsPrincipal principal,
            ControlPlaneDbContext db) =>
        {
            var target = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, context.RequestAborted);
            if (target is null) return Results.NotFound();

            var currentAdminId = CurrentUserId(principal);
            if (target.Id == currentAdminId)
                return AdminErrorPage(context, principal, "You cannot disable your own administrator account.", target.Id);

            if (target.IsDisabled)
                return Results.Redirect($"/admin/users/{target.Id}");

            var enabledAdminCount = target.IsAdmin
                ? await db.Users.CountAsync(x => x.IsAdmin && !x.IsDisabled, context.RequestAborted)
                : 2;

            if (target.IsAdmin && enabledAdminCount <= 1)
                return AdminErrorPage(context, principal, "This is the last enabled administrator account and cannot be disabled.", target.Id);

            var body = $"""
                {PortalUi.PageHeading("Administration", "Disable user account", "This action takes effect on the user's web sessions and Agent authorization immediately.")}
                <section class="danger-confirm-card" aria-labelledby="disable-user-title">
                  <div class="danger-confirm-icon" aria-hidden="true">!</div>
                  <div>
                    <h2 id="disable-user-title">Disable {PortalUi.H(target.Email)}?</h2>
                    <p>The user will be unable to sign in. Existing authenticated web sessions will be rejected, and enrolled Agents owned by this account will stop authenticating until the account is enabled again.</p>
                    <dl class="confirm-device-meta">
                      <div><dt>Account</dt><dd>{PortalUi.H(target.Email)}</dd></div>
                      <div><dt>Role</dt><dd>{(target.IsAdmin ? "Administrator" : "User")}</dd></div>
                      <div><dt>User ID</dt><dd><code>{target.Id}</code></dd></div>
                    </dl>
                    <div class="danger-confirm-actions">
                      <a class="button button-secondary" href="/admin/users/{target.Id}">Cancel</a>
                      <form method="post" action="/admin/users/{target.Id}/disable"><button class="button button-danger" type="submit">Disable account</button></form>
                    </div>
                  </div>
                </section>
                """;

            return PortalUi.AppPage(context, "Confirm account disable", principal.Identity?.Name ?? "MateMCP administrator", body, "admin");
        });

        group.MapPost("/users/{userId:guid}/disable", async (
            Guid userId,
            HttpContext context,
            ClaimsPrincipal principal,
            ControlPlaneDbContext db) =>
        {
            var currentAdminId = CurrentUserId(principal);
            if (userId == currentAdminId) return Results.BadRequest();

            var target = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, context.RequestAborted);
            if (target is null) return Results.NotFound();
            if (target.IsDisabled) return Results.Redirect($"/admin/users/{target.Id}");

            if (target.IsAdmin)
            {
                var enabledAdminCount = await db.Users.CountAsync(x => x.IsAdmin && !x.IsDisabled, context.RequestAborted);
                if (enabledAdminCount <= 1) return Results.Conflict();
            }

            target.IsDisabled = true;
            db.AuditEvents.Add(new AuditEvent { UserId = currentAdminId, EventType = "admin.user.disabled", Detail = target.Id.ToString() });
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.Redirect($"/admin/users/{target.Id}?notice=disabled");
        });

        group.MapPost("/users/{userId:guid}/enable", async (
            Guid userId,
            HttpContext context,
            ClaimsPrincipal principal,
            ControlPlaneDbContext db) =>
        {
            var target = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, context.RequestAborted);
            if (target is null) return Results.NotFound();
            if (!target.IsDisabled) return Results.Redirect($"/admin/users/{target.Id}");

            target.IsDisabled = false;
            db.AuditEvents.Add(new AuditEvent { UserId = CurrentUserId(principal), EventType = "admin.user.enabled", Detail = target.Id.ToString() });
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.Redirect($"/admin/users/{target.Id}?notice=enabled");
        });

        group.MapGet("/devices/{agentId}/revoke", async (
            string agentId,
            HttpContext context,
            ClaimsPrincipal principal,
            ControlPlaneDbContext db) =>
        {
            var agent = await db.Agents.Include(x => x.Owner).AsNoTracking()
                .SingleOrDefaultAsync(x => x.PublicId == agentId && !x.IsRevoked, context.RequestAborted);

            if (agent is null) return Results.NotFound();

            var body = $"""
                {PortalUi.PageHeading("Administration", "Revoke user device", "This permanently invalidates the selected device credential.")}
                <section class="danger-confirm-card" aria-labelledby="admin-revoke-title">
                  <div class="danger-confirm-icon" aria-hidden="true">!</div>
                  <div>
                    <h2 id="admin-revoke-title">Revoke {PortalUi.H(agent.Name)}?</h2>
                    <p>This device belongs to {PortalUi.H(agent.Owner?.Email ?? "the selected user")}. It will immediately lose Agent access and move to revoked history.</p>
                    <dl class="confirm-device-meta">
                      <div><dt>Device</dt><dd>{PortalUi.H(agent.Name)}</dd></div>
                      <div><dt>Owner</dt><dd>{PortalUi.H(agent.Owner?.Email ?? "Unknown")}</dd></div>
                      <div><dt>Device ID</dt><dd><code>{PortalUi.H(agent.PublicId)}</code></dd></div>
                    </dl>
                    <div class="danger-confirm-actions">
                      <a class="button button-secondary" href="/admin/users/{agent.OwnerId}">Cancel</a>
                      <form method="post" action="/admin/devices/{PortalUi.H(agent.PublicId)}/revoke"><button class="button button-danger" type="submit">Revoke device</button></form>
                    </div>
                  </div>
                </section>
                """;

            return PortalUi.AppPage(context, "Confirm admin device revoke", principal.Identity?.Name ?? "MateMCP administrator", body, "admin");
        });

        group.MapPost("/devices/{agentId}/revoke", async (
            string agentId,
            HttpContext context,
            ClaimsPrincipal principal,
            ControlPlaneDbContext db) =>
        {
            var agent = await db.Agents.SingleOrDefaultAsync(x => x.PublicId == agentId && !x.IsRevoked, context.RequestAborted);
            if (agent is null) return Results.NotFound();

            agent.IsRevoked = true;
            db.AuditEvents.Add(new AuditEvent
            {
                UserId = CurrentUserId(principal),
                AgentDeviceId = agent.Id,
                EventType = "admin.agent.revoked",
                Detail = agent.OwnerId.ToString()
            });
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.Redirect($"/admin/users/{agent.OwnerId}?notice=device-revoked");
        });
    }

    private static string UserRow(UserAccount user)
    {
        var activeDevices = user.Agents.Count(x => !x.IsRevoked);
        return $"""
            <article class="admin-user-row">
              <div class="admin-user-identity">
                <span class="admin-user-avatar" aria-hidden="true">{Initial(user.Email)}</span>
                <div><strong>{PortalUi.H(user.Email)}</strong><span>{PortalUi.H(user.Id.ToString())}</span></div>
              </div>
              <div class="admin-user-meta">
                <span class="role-badge{(user.IsAdmin ? " role-admin" : "")}">{(user.IsAdmin ? "Administrator" : "User")}</span>
                {PortalUi.StatusBadge(user.IsDisabled ? "disabled" : "active")}
              </div>
              <div class="admin-user-stat"><strong>{activeDevices}</strong><span>active devices</span></div>
              <a class="button button-secondary button-compact" href="/admin/users/{user.Id}">Manage</a>
            </article>
            """;
    }

    private static string AdminDeviceRow(AgentDevice agent)
    {
        var online = !agent.IsRevoked && agent.LastSeenAt is not null && agent.LastSeenAt > DateTimeOffset.UtcNow.AddMinutes(-2);
        var status = agent.IsRevoked ? "revoked" : online ? "online" : "offline";

        return $"""
            <article class="admin-device-row">
              <div class="device-name">
                <span class="device-icon" aria-hidden="true">{PlatformInitial(agent.Platform)}</span>
                <div><strong>{PortalUi.H(agent.Name)}</strong><span>{PortalUi.H(agent.Platform)} · {PortalUi.H(agent.PublicId)}</span></div>
              </div>
              {PortalUi.StatusBadge(status)}
              <span class="admin-device-lastseen">{PortalUi.H(FormatLastSeen(agent.LastSeenAt))}</span>
              {(agent.IsRevoked
                  ? """<span class="admin-device-history">Revoked history</span>"""
                  : $"""<a class="button button-danger button-compact" href="/admin/devices/{PortalUi.H(agent.PublicId)}/revoke">Revoke</a>""")}
            </article>
            """;
    }

    private static IResult AdminErrorPage(HttpContext context, ClaimsPrincipal principal, string message, Guid userId)
    {
        var body = $"""
            {PortalUi.PageHeading("Administration", "Action not available", "The requested account change was blocked by an administrator safety rule.")}
            <section class="portal-section"><div class="form-card">
              {PortalUi.Alert(message, "warning")}
              <a class="button button-secondary" href="/admin/users/{userId}">Back to account</a>
            </div></section>
            """;

        return PortalUi.AppPage(
            context,
            "Action not available",
            principal.Identity?.Name ?? "MateMCP administrator",
            body,
            "admin",
            StatusCodes.Status409Conflict);
    }

    private static char Initial(string value)
        => string.IsNullOrWhiteSpace(value) ? 'U' : char.ToUpperInvariant(value.Trim()[0]);

    private static string PlatformInitial(string value)
        => string.IsNullOrWhiteSpace(value) ? "D" : PortalUi.H(value.Trim()[..1].ToUpperInvariant());

    private static string FormatDate(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'");

    private static string FormatLastSeen(DateTimeOffset? value)
    {
        if (value is null) return "Never";
        var elapsed = DateTimeOffset.UtcNow - value.Value;
        if (elapsed < TimeSpan.FromMinutes(1)) return "Just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)elapsed.TotalMinutes)} min ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)elapsed.TotalHours)} hr ago";
        return FormatDate(value.Value);
    }

    private static Guid CurrentUserId(ClaimsPrincipal principal)
        => Guid.Parse(principal.FindFirst(ClaimTypes.NameIdentifier)!.Value);
}
