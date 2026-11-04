using System.Security.Claims;
using MateMCP.Api.Data;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace MateMCP.Api.Portal;

public static class PortalConnectedAppEndpoints
{
    private const int MaximumAuthorizationRows = 100;

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/connected-apps").RequireAuthorization();

        group.MapGet("", async (
            HttpContext context,
            ClaimsPrincipal principal,
            ControlPlaneDbContext db,
            IOpenIddictAuthorizationManager authorizations,
            IOpenIddictApplicationManager applications,
            string? notice = null) =>
        {
            var ownerId = UserId(principal);
            var rows = new List<ConnectedAuthorization>();

            await foreach (var authorization in authorizations.FindBySubjectAsync(
                ownerId.ToString(), context.RequestAborted))
            {
                if (rows.Count >= MaximumAuthorizationRows)
                    break;

                if (!string.Equals(
                    await authorizations.GetTypeAsync(authorization, context.RequestAborted),
                    OpenIddictConstants.AuthorizationTypes.Permanent,
                    StringComparison.Ordinal))
                    continue;

                var applicationId = await authorizations.GetApplicationIdAsync(authorization, context.RequestAborted);
                if (string.IsNullOrWhiteSpace(applicationId))
                    continue;

                var application = await applications.FindByIdAsync(applicationId, context.RequestAborted);
                if (application is null)
                    continue;

                var properties = await authorizations.GetPropertiesAsync(authorization, context.RequestAborted);
                var agentId = ReadString(properties, "agent_id");
                var callbackHost = ReadString(properties, "redirect_host");
                var authorizationId = await authorizations.GetIdAsync(authorization, context.RequestAborted);
                if (string.IsNullOrWhiteSpace(authorizationId))
                    continue;

                rows.Add(new ConnectedAuthorization(
                    authorizationId,
                    await applications.GetDisplayNameAsync(application, context.RequestAborted) ?? "MCP client",
                    agentId,
                    callbackHost,
                    await authorizations.GetStatusAsync(authorization, context.RequestAborted) ?? "unknown",
                    await authorizations.GetScopesAsync(authorization, context.RequestAborted),
                    await authorizations.GetCreationDateAsync(authorization, context.RequestAborted)));
            }

            var agentIds = rows.Select(x => x.AgentId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var agents = agentIds.Length == 0
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : await db.Agents.AsNoTracking()
                    .Where(x => x.OwnerId == ownerId && agentIds.Contains(x.PublicId))
                    .ToDictionaryAsync(x => x.PublicId, x => x.Name, StringComparer.Ordinal, context.RequestAborted);

            rows = rows.Select(x => x with
                {
                    AgentName = x.AgentId is not null && agents.TryGetValue(x.AgentId, out var name)
                        ? name
                        : x.AgentId is null ? "Unknown Agent" : "Agent no longer available"
                })
                .OrderByDescending(x => x.CreatedAt ?? DateTimeOffset.MinValue)
                .ToList();

            var activeCount = rows.Count(x => x.Status == OpenIddictConstants.Statuses.Valid);
            var noticeMarkup = notice switch
            {
                "revoked" => PortalUi.Alert("This MCP client has been disconnected. Other clients and enrolled devices remain connected.", "success"),
                "already-revoked" => PortalUi.Alert("This connection was already disconnected.", "warning"),
                _ => string.Empty
            };

            var content = rows.Count == 0
                ? PortalUi.EmptyState(
                    "No connected MCP clients",
                    "Authorize an MCP client using the stable Relay endpoint to manage its connection here.")
                : $"""
                  <div class="connected-app-list">
                    {string.Join("", rows.Select(ConnectedAppCard))}
                  </div>
                  """;

            var body = $"""
                {PortalUi.PageHeading(
                    "OAuth access",
                    "Connected apps",
                    "Manage individual MCP client authorizations. Disconnecting an app does not revoke the Agent device itself or affect other clients.")}
                {noticeMarkup}
                <section class="connected-app-overview" aria-label="Connected app summary">
                  <div><strong>{activeCount}</strong><span>Active connections</span></div>
                  <div><strong>{rows.Count - activeCount}</strong><span>Revoked connections</span></div>
                </section>
                <section class="portal-section">
                  <div class="portal-section-header">
                    <div><h2>MCP client authorizations</h2><p>Each authorization is bound to one Agent and can be disconnected independently.</p></div>
                    <span class="section-count">{rows.Count}</span>
                  </div>
                  {content}
                </section>
                """;

            return PortalUi.AppPage(
                context,
                "Connected apps",
                principal.Identity?.Name ?? "MateMCP user",
                body,
                "connected-apps");
        });

        group.MapGet("/{authorizationId}/revoke", async (
            string authorizationId,
            HttpContext context,
            ClaimsPrincipal principal,
            IOpenIddictAuthorizationManager authorizations,
            IOpenIddictApplicationManager applications,
            ControlPlaneDbContext db) =>
        {
            var item = await FindOwnedAuthorizationAsync(
                authorizationId, UserId(principal), authorizations, applications, db, context.RequestAborted);
            if (item is null || item.Status != OpenIddictConstants.Statuses.Valid)
                return Results.NotFound();

            var body = $"""
                {PortalUi.PageHeading(
                    "OAuth access",
                    "Disconnect MCP client",
                    "This action immediately disables the selected authorization. It does not revoke the underlying Agent device.")}
                <section class="danger-confirm-card" aria-labelledby="connected-app-revoke-title">
                  <div class="danger-confirm-icon" aria-hidden="true">!</div>
                  <div>
                    <h2 id="connected-app-revoke-title">Disconnect {PortalUi.H(item.ClientName)}?</h2>
                    <p>Only this authorization will be disconnected. Other MCP clients using the same or different Agents will remain active.</p>
                    <dl class="confirm-device-meta">
                      <div><dt>Agent</dt><dd>{PortalUi.H(item.AgentName)}</dd></div>
                      <div><dt>Callback host</dt><dd><code>{PortalUi.H(item.CallbackHost ?? "Not provided")}</code></dd></div>
                      <div><dt>Created</dt><dd>{PortalUi.H(FormatDate(item.CreatedAt))}</dd></div>
                    </dl>
                    <div class="danger-confirm-actions">
                      <a class="button button-secondary" href="/connected-apps">Cancel</a>
                      <form method="post" action="/connected-apps/{Uri.EscapeDataString(authorizationId)}/revoke">
                        <button class="button button-danger" type="submit">Disconnect app</button>
                      </form>
                    </div>
                  </div>
                </section>
                """;

            return PortalUi.AppPage(
                context,
                "Disconnect MCP client",
                principal.Identity?.Name ?? "MateMCP user",
                body,
                "connected-apps");
        });

        group.MapPost("/{authorizationId}/revoke", async (
            string authorizationId,
            HttpContext context,
            ClaimsPrincipal principal,
            IOpenIddictAuthorizationManager authorizations,
            IOpenIddictApplicationManager applications,
            ControlPlaneDbContext db) =>
        {
            var ownerId = UserId(principal);
            var item = await FindOwnedAuthorizationAsync(
                authorizationId, ownerId, authorizations, applications, db, context.RequestAborted);
            if (item is null)
                return Results.NotFound();

            if (item.Status != OpenIddictConstants.Statuses.Valid)
                return Results.Redirect("/connected-apps?notice=already-revoked");

            var authorization = await authorizations.FindByIdAsync(authorizationId, context.RequestAborted);
            if (authorization is null || !await authorizations.TryRevokeAsync(authorization, context.RequestAborted))
                return Results.Redirect("/connected-apps?notice=already-revoked");

            var agent = item.AgentId is null
                ? null
                : await db.Agents.SingleOrDefaultAsync(
                    x => x.OwnerId == ownerId && x.PublicId == item.AgentId,
                    context.RequestAborted);
            db.AuditEvents.Add(new AuditEvent
            {
                UserId = ownerId,
                AgentDeviceId = agent?.Id,
                EventType = "oauth.authorization.revoked",
                Detail = $"{Bounded(item.ClientName, 80)} | {Bounded(item.AgentName, 80)} | {Bounded(item.CallbackHost, 120)}"
            });
            await db.SaveChangesAsync(context.RequestAborted);
            return Results.Redirect("/connected-apps?notice=revoked");
        });
    }

    private static async Task<ConnectedAuthorization?> FindOwnedAuthorizationAsync(
        string authorizationId,
        Guid ownerId,
        IOpenIddictAuthorizationManager authorizations,
        IOpenIddictApplicationManager applications,
        ControlPlaneDbContext db,
        CancellationToken cancellationToken)
    {
        var authorization = await authorizations.FindByIdAsync(authorizationId, cancellationToken);
        if (authorization is null ||
            !string.Equals(await authorizations.GetSubjectAsync(authorization, cancellationToken), ownerId.ToString(), StringComparison.Ordinal) ||
            !string.Equals(await authorizations.GetTypeAsync(authorization, cancellationToken), OpenIddictConstants.AuthorizationTypes.Permanent, StringComparison.Ordinal))
            return null;

        var applicationId = await authorizations.GetApplicationIdAsync(authorization, cancellationToken);
        if (string.IsNullOrWhiteSpace(applicationId)) return null;
        var application = await applications.FindByIdAsync(applicationId, cancellationToken);
        if (application is null) return null;

        var properties = await authorizations.GetPropertiesAsync(authorization, cancellationToken);
        var agentId = ReadString(properties, "agent_id");
        var callbackHost = ReadString(properties, "redirect_host");
        var agentName = "Unknown Agent";
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            agentName = await db.Agents.AsNoTracking()
                .Where(x => x.OwnerId == ownerId && x.PublicId == agentId)
                .Select(x => x.Name)
                .SingleOrDefaultAsync(cancellationToken) ?? "Agent no longer available";
        }

        return new ConnectedAuthorization(
            authorizationId,
            await applications.GetDisplayNameAsync(application, cancellationToken) ?? "MCP client",
            agentId,
            callbackHost,
            await authorizations.GetStatusAsync(authorization, cancellationToken) ?? "unknown",
            await authorizations.GetScopesAsync(authorization, cancellationToken),
            await authorizations.GetCreationDateAsync(authorization, cancellationToken))
        {
            AgentName = agentName
        };
    }

    private static string ConnectedAppCard(ConnectedAuthorization item)
    {
        var active = item.Status == OpenIddictConstants.Statuses.Valid;
        var status = active ? "active" : "revoked";
        var scopes = string.Join(" ", item.Scopes.OrderBy(x => x, StringComparer.Ordinal));
        var actions = active
            ? $"<a class=\"button button-danger button-compact\" href=\"/connected-apps/{Uri.EscapeDataString(item.Id)}/revoke\">Disconnect</a>"
            : "";

        return $"""
            <article class="portal-section connected-app-card">
              <div class="portal-section-header">
                <div><h3>{PortalUi.H(item.ClientName)}</h3><p>Callback host: <code>{PortalUi.H(item.CallbackHost ?? "Not provided")}</code></p></div>
                {PortalUi.StatusBadge(status)}
              </div>
              <dl class="detail-list">
                <div><dt>Bound Agent</dt><dd>{PortalUi.H(item.AgentName)}</dd></div>
                <div><dt>Created</dt><dd>{PortalUi.H(FormatDate(item.CreatedAt))}</dd></div>
                <div><dt>Scopes</dt><dd><code>{PortalUi.H(scopes)}</code></dd></div>
              </dl>
              <div class="device-card-footer">{actions}</div>
            </article>
            """;
    }

    private static string? ReadString(IReadOnlyDictionary<string, System.Text.Json.JsonElement> properties, string key)
        => properties.TryGetValue(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FormatDate(DateTimeOffset? value)
        => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz") ?? "Unknown";

    private static string Bounded(string? value, int maxLength)
    {
        var normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static Guid UserId(ClaimsPrincipal principal)
        => Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private sealed record ConnectedAuthorization(
        string Id,
        string ClientName,
        string? AgentId,
        string? CallbackHost,
        string Status,
        IReadOnlyCollection<string> Scopes,
        DateTimeOffset? CreatedAt)
    {
        public string AgentName { get; init; } = "Unknown Agent";
    }
}
