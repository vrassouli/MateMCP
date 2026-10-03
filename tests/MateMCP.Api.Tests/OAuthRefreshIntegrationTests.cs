using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MateMCP.Api;
using MateMCP.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

[Collection("API integration serial")]
public sealed class OAuthRefreshIntegrationTests : IAsyncLifetime
{
    private const string ApiUrl = "https://api.test";
    private const string RelayUrl = "https://relay.test";
    private const string AgentId = "agt_refresh_test";
    private const string InternalKey = "oauth-refresh-integration-internal-key";
    private const string Email = "oauth-refresh@example.test";
    private const string Password = "oauth-refresh-password";
    private const string AdminEmail = "admin@example.test";
    private const string AdminPassword = "admin-integration-password";
    private const string RedirectUri = "https://client.test/callback";

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "matemcp-oauth-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _adminId = Guid.NewGuid();
    private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.Ordinal);
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempRoot);
        SetEnvironment("MateMCP__PublicUrl", ApiUrl);
        SetEnvironment("MateMCP__RelayUrl", RelayUrl);
        SetEnvironment("MateMCP__DatabaseProvider", "sqlite");
        SetEnvironment("ConnectionStrings__MateMCP", $"Data Source={Path.Combine(_tempRoot, "matemcp.db")}");
        SetEnvironment("MateMCP__InternalApiKey", InternalKey);
        SetEnvironment("MateMCP__KeyPath", Path.Combine(_tempRoot, "keys"));

        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(ApiUrl),
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        using (var health = await _client.GetAsync("/health"))
            health.EnsureSuccessStatusCode();

        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<UserAccount>>();
            var user = new UserAccount
            {
                Id = _ownerId,
                Email = Email,
                NormalizedEmail = Email.ToUpperInvariant(),
                PasswordHash = "pending"
            };
            user.PasswordHash = hasher.HashPassword(user, Password);
            var admin = new UserAccount
            {
                Id = _adminId,
                Email = AdminEmail,
                NormalizedEmail = AdminEmail.ToUpperInvariant(),
                PasswordHash = "pending",
                IsAdmin = true
            };
            admin.PasswordHash = hasher.HashPassword(admin, AdminPassword);
            db.Users.AddRange(user, admin);
            db.Agents.Add(new AgentDevice
            {
                PublicId = AgentId,
                OwnerId = _ownerId,
                Name = "OAuth refresh integration Agent",
                Platform = "test",
                CredentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("agent-credential"))),
                AllowedScopes = "mcp:read mcp:write"
            });
            await db.SaveChangesAsync();
        }

        using var login = await PostPortalFormAsync(_client, "/login", "/login", new Dictionary<string, string>
        {
            ["email"] = Email,
            ["password"] = Password,
            ["returnUrl"] = "/dashboard"
        });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
    }

    [Theory]
    [InlineData("/.well-known/oauth-authorization-server")]
    [InlineData("/.well-known/oauth-authorization-server/")]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/.well-known/openid-configuration/")]
    public async Task OAuthDiscovery_IsConsistent_WithOrWithoutTrailingSlash(string path)
    {
        using var response = await _client!.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var root = json.RootElement;
        Assert.Equal(ApiUrl + "/", root.GetProperty("issuer").GetString());
        Assert.Equal(ApiUrl + "/connect/register", root.GetProperty("registration_endpoint").GetString());
        Assert.Contains("none", root.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("S256", root.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task DynamicClientRegistration_ReturnsRfc7591CreatedResponse()
    {
        using var response = await _client!.PostAsJsonAsync("/connect/register", new
        {
            client_name = "RFC 7591 registration probe",
            redirect_uris = new[] { RedirectUri },
            token_endpoint_auth_method = "none",
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            application_type = "web"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains(response.Headers.Pragma, value => string.Equals(value.Name, "no-cache", StringComparison.OrdinalIgnoreCase));

        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("client_id").GetString()));
        Assert.Equal("none", json.RootElement.GetProperty("token_endpoint_auth_method").GetString());
    }

    [Fact]
    public async Task AuthorizationCode_WithOfflineAccess_IssuesRefreshToken_AndRefreshPreservesBinding()
    {
        var client = _client!;

        using var registration = await client.PostAsJsonAsync("/connect/register", new
        {
            client_name = "OAuth refresh integration client",
            redirect_uris = new[] { RedirectUri }
        });
        registration.EnsureSuccessStatusCode();
        using var registrationJson = JsonDocument.Parse(await registration.Content.ReadAsStreamAsync());
        var clientId = registrationJson.RootElement.GetProperty("client_id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(clientId));

        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var resource = $"{RelayUrl}/mcp/{AgentId}";
        var authorizeUrl = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "mcp:read mcp:shell offline_access",
            ["resource"] = resource,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = "refresh-test-state"
        });

        using var authorization = await client.GetAsync(authorizeUrl);
        Assert.Equal(HttpStatusCode.Redirect, authorization.StatusCode);
        Assert.NotNull(authorization.Headers.Location);
        Assert.Equal("client.test", authorization.Headers.Location!.Host);
        var authorizationQuery = QueryHelpers.ParseQuery(authorization.Headers.Location.Query);
        var code = authorizationQuery["code"].ToString();
        Assert.False(string.IsNullOrWhiteSpace(code));
        Assert.Equal(ApiUrl + "/", authorizationQuery["iss"].ToString());

        using var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId!,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
            ["resource"] = resource
        }));
        token.EnsureSuccessStatusCode();
        using var tokenJson = JsonDocument.Parse(await token.Content.ReadAsStreamAsync());
        var accessToken = tokenJson.RootElement.GetProperty("access_token").GetString();
        var refreshToken = tokenJson.RootElement.GetProperty("refresh_token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(accessToken));
        Assert.False(string.IsNullOrWhiteSpace(refreshToken));
        AssertScopes(tokenJson.RootElement, required: ["mcp:read", "offline_access"], forbidden: ["mcp:shell"]);
        AssertAccessTokenBinding(accessToken!, resource);

        using var refresh = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId!,
            ["refresh_token"] = refreshToken!
        }));
        refresh.EnsureSuccessStatusCode();
        using var refreshJson = JsonDocument.Parse(await refresh.Content.ReadAsStreamAsync());
        var refreshedAccessToken = refreshJson.RootElement.GetProperty("access_token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(refreshedAccessToken));
        AssertScopes(refreshJson.RootElement, required: ["mcp:read", "offline_access"], forbidden: ["mcp:shell"]);
        AssertAccessTokenBinding(refreshedAccessToken!, resource);

        using var authorized = await PostInternalAuthorizeAsync(["mcp:read", "offline_access"]);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);

        using var escalated = await PostInternalAuthorizeAsync(["mcp:read", "mcp:shell", "offline_access"]);
        Assert.Equal(HttpStatusCode.Forbidden, escalated.StatusCode);

        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var agent = await db.Agents.SingleAsync(x => x.PublicId == AgentId);
            agent.IsRevoked = true;
            await db.SaveChangesAsync();
        }

        using var revoked = await PostInternalAuthorizeAsync(["mcp:read", "offline_access"]);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }

    [Fact]
    public async Task Portal_auth_pages_are_branded_responsive_and_no_store()
    {
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(ApiUrl),
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        using var login = await client.GetAsync("/login?returnUrl=%2Fdashboard");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var loginHtml = await login.Content.ReadAsStringAsync();
        Assert.Contains("class=\"auth-layout\"", loginHtml, StringComparison.Ordinal);
        Assert.Contains("/portal/portal.css", loginHtml, StringComparison.Ordinal);
        Assert.Contains("/portal/analytics.js", loginHtml, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"current-password\"", loginHtml, StringComparison.Ordinal);
        Assert.Contains("Create an account", loginHtml, StringComparison.Ordinal);
        Assert.True(login.Headers.CacheControl?.NoStore);
        Assert.True(login.Headers.TryGetValues("Content-Security-Policy", out var csp));
        Assert.Contains(csp, value => value.Contains("frame-ancestors 'none'", StringComparison.Ordinal));
        Assert.Contains(csp, value => value.Contains("script-src 'self' https://static.cloudflareinsights.com https://*.googletagmanager.com", StringComparison.Ordinal));
        Assert.Contains(csp, value => value.Contains("connect-src 'self' https://cloudflareinsights.com https://*.google-analytics.com", StringComparison.Ordinal));

        using var register = await client.GetAsync("/register");
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);
        var registerHtml = await register.Content.ReadAsStringAsync();
        Assert.Contains("Ready to keep working?", registerHtml, StringComparison.Ordinal);
        Assert.Contains("Built-in agent hit a limit?", registerHtml, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"new-password\"", registerHtml, StringComparison.Ordinal);
        Assert.Contains("minlength=\"10\"", registerHtml, StringComparison.Ordinal);

        using var styles = await client.GetAsync("/portal/portal.css");
        styles.EnsureSuccessStatusCode();
        var css = await styles.Content.ReadAsStringAsync();
        Assert.Contains(":focus-visible", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 980px)", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 820px)", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 560px)", css, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_login_rerenders_the_form_without_echoing_password()
    {
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(ApiUrl),
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        const string attemptedPassword = "definitely-the-wrong-password";
        using var response = await PostPortalFormAsync(client, "/login", "/login", new Dictionary<string, string>
        {
            ["email"] = Email,
            ["password"] = attemptedPassword,
            ["returnUrl"] = "/dashboard"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("The email or password is incorrect.", html, StringComparison.Ordinal);
        Assert.Contains($"value=\"{Email}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain(attemptedPassword, html, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Authenticated_dashboard_uses_the_account_shell_and_identity()
    {
        using var response = await _client!.GetAsync("/dashboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("class=\"portal-app\"", html, StringComparison.Ordinal);
        Assert.Contains("MateMCP", html, StringComparison.Ordinal);
        Assert.Contains("Control", html, StringComparison.Ordinal);
        Assert.Contains(Email, html, StringComparison.Ordinal);
        Assert.Contains("Pending approvals", html, StringComparison.Ordinal);
        Assert.Contains("action=\"/logout\"", html, StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
    [Fact]
    public async Task Devices_page_keeps_revoked_history_out_of_the_active_working_list()
    {
        const string revokedId = "agt_revoked_history";
        const string foreignId = "agt_foreign_owner";
        var foreignOwnerId = Guid.NewGuid();

        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            db.Agents.Add(new AgentDevice
            {
                PublicId = revokedId,
                OwnerId = _ownerId,
                Name = "Old revoked device",
                Platform = "windows",
                CredentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("revoked-device-token"))),
                IsRevoked = true
            });
            db.Users.Add(new UserAccount
            {
                Id = foreignOwnerId,
                Email = "foreign-device-owner@example.test",
                NormalizedEmail = "FOREIGN-DEVICE-OWNER@EXAMPLE.TEST",
                PasswordHash = "not-used"
            });
            db.Agents.Add(new AgentDevice
            {
                PublicId = foreignId,
                OwnerId = foreignOwnerId,
                Name = "Someone else's device",
                Platform = "macos",
                CredentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("foreign-device-token")))
            });
            await db.SaveChangesAsync();
        }

        using var activeResponse = await _client!.GetAsync("/devices");
        Assert.Equal(HttpStatusCode.OK, activeResponse.StatusCode);
        var activeHtml = await activeResponse.Content.ReadAsStringAsync();
        Assert.Contains(AgentId, activeHtml, StringComparison.Ordinal);
        Assert.Contains("Revoked history", activeHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(revokedId, activeHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(foreignId, activeHtml, StringComparison.Ordinal);

        using var historyResponse = await _client.GetAsync("/devices?showRevoked=true");
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        var historyHtml = await historyResponse.Content.ReadAsStringAsync();
        Assert.Contains(revokedId, historyHtml, StringComparison.Ordinal);
        Assert.Contains("Old revoked device", historyHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(foreignId, historyHtml, StringComparison.Ordinal);

        using var dashboardResponse = await _client.GetAsync("/dashboard");
        var dashboardHtml = await dashboardResponse.Content.ReadAsStringAsync();
        Assert.Contains(AgentId, dashboardHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(revokedId, dashboardHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Device_revoke_requires_confirmation_then_removes_device_from_active_list()
    {
        using var confirmation = await _client!.GetAsync($"/devices/{AgentId}/revoke");
        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        var confirmationHtml = await confirmation.Content.ReadAsStringAsync();
        Assert.Contains("Revoke device access", confirmationHtml, StringComparison.Ordinal);
        Assert.Contains($"action=\"/devices/{AgentId}/revoke\"", confirmationHtml, StringComparison.Ordinal);
        Assert.Contains("security history will remain available", confirmationHtml, StringComparison.Ordinal);

        using var revoke = await PostPortalFormAsync(_client, $"/devices/{AgentId}/revoke", $"/devices/{AgentId}/revoke");
        Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);
        Assert.NotNull(revoke.Headers.Location);
        Assert.Equal("/devices?notice=revoked", revoke.Headers.Location.OriginalString);

        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var agent = await db.Agents.SingleAsync(x => x.PublicId == AgentId);
            Assert.True(agent.IsRevoked);
            Assert.True(await db.AuditEvents.AnyAsync(x =>
                x.AgentDeviceId == agent.Id &&
                x.UserId == _ownerId &&
                x.EventType == "agent.revoked"));
        }

        using var activeResponse = await _client.GetAsync("/devices?notice=revoked");
        var activeHtml = await activeResponse.Content.ReadAsStringAsync();
        Assert.Contains("removed from your active device list", activeHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(AgentId, activeHtml, StringComparison.Ordinal);

        using var historyResponse = await _client.GetAsync("/devices?showRevoked=true");
        var historyHtml = await historyResponse.Content.ReadAsStringAsync();
        Assert.Contains(AgentId, historyHtml, StringComparison.Ordinal);
        Assert.Contains("revoked", historyHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Device_management_never_allows_access_to_another_owners_device()
    {
        const string foreignId = "agt_not_yours";
        var foreignOwnerId = Guid.NewGuid();
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            db.Users.Add(new UserAccount
            {
                Id = foreignOwnerId,
                Email = "not-your-device-owner@example.test",
                NormalizedEmail = "NOT-YOUR-DEVICE-OWNER@EXAMPLE.TEST",
                PasswordHash = "not-used"
            });
            db.Agents.Add(new AgentDevice
            {
                PublicId = foreignId,
                OwnerId = foreignOwnerId,
                Name = "Foreign device",
                Platform = "windows",
                CredentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("foreign-owner-token")))
            });
            await db.SaveChangesAsync();
        }

        using var details = await _client!.GetAsync($"/devices/{foreignId}");
        Assert.Equal(HttpStatusCode.NotFound, details.StatusCode);

        using var confirmation = await _client.GetAsync($"/devices/{foreignId}/revoke");
        Assert.Equal(HttpStatusCode.NotFound, confirmation.StatusCode);

        using var revoke = await PostPortalFormAsync(_client, $"/devices/{foreignId}/revoke", "/dashboard");
        Assert.Equal(HttpStatusCode.NotFound, revoke.StatusCode);

        await using var verifyScope = _factory!.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        Assert.False((await verifyDb.Agents.SingleAsync(x => x.PublicId == foreignId)).IsRevoked);
    }

    [Fact]
    public async Task Device_details_show_connection_metadata_but_never_render_credentials()
    {
        string credentialHash;
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            credentialHash = (await db.Agents.AsNoTracking().SingleAsync(x => x.PublicId == AgentId)).CredentialHash;
        }

        using var response = await _client!.GetAsync($"/devices/{AgentId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains($"{RelayUrl}/mcp/{AgentId}", html, StringComparison.Ordinal);
        Assert.Contains("Device ID", html, StringComparison.Ordinal);
        Assert.Contains("Last seen", html, StringComparison.Ordinal);
        Assert.DoesNotContain(credentialHash, html, StringComparison.Ordinal);
        Assert.DoesNotContain("CredentialHash", html, StringComparison.Ordinal);
    }
    [Fact]
    public async Task Agent_activity_internal_endpoint_requires_the_internal_key()
    {
        using var response = await _client!.PostAsJsonAsync("/internal/agents/activity", new
        {
            agentId = AgentId,
            type = "connected",
            status = "success",
            operation = "Relay connection",
            message = "Agent connected."
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Device_activity_is_owner_scoped_filterable_and_redacts_sensitive_values()
    {
        using (var connected = await PostInternalActivityAsync(new
        {
            agentId = AgentId,
            type = "connected",
            status = "success",
            operation = "Relay connection",
            message = "Agent connected."
        }))
            Assert.Equal(HttpStatusCode.OK, connected.StatusCode);

        using (var failed = await PostInternalActivityAsync(new
        {
            agentId = AgentId,
            type = "request",
            status = "failure",
            operation = "tools/call · shell_exec",
            message = "token=TOP-SECRET; Bearer abc.def.ghi failed upstream",
            durationMs = 123.4,
            requestId = "req_activity_test",
            project = "MateMCP"
        }))
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);

        await using (var verifyActivityScope = _factory!.Services.CreateAsyncScope())
        {
            var verifyActivityDb = verifyActivityScope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var audit = await verifyActivityDb.AuditEvents.AsNoTracking()
                .Where(x => x.EventType == "runtime.request")
                .OrderByDescending(x => x.Id)
                .FirstAsync();
            Assert.Contains("shell_exec", audit.Detail, StringComparison.Ordinal);
            var entry = AgentActivityEndpoints.ToEntry(audit);
            Assert.NotNull(entry);
            Assert.Contains("shell_exec", entry.Operation, StringComparison.Ordinal);
            Assert.Equal("failure", entry.Status);
            Assert.Equal("MateMCP", entry.Project);
        }

        using var response = await _client!.GetAsync($"/devices/{AgentId}?activityType=request&activityStatus=failure&activityProject=MateMCP&q=shell_exec");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var decodedHtml = WebUtility.HtmlDecode(html);

        Assert.Contains("Recent activity", html, StringComparison.Ordinal);
        Assert.Contains("Last activity", html, StringComparison.Ordinal);
        Assert.Contains("tools/call · shell_exec", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("failure", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("123 ms", html, StringComparison.Ordinal);
        Assert.Contains("Project MateMCP", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("50 per page", decodedHtml, StringComparison.Ordinal);
        Assert.Contains("[redacted]", decodedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("TOP-SECRET", html, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def.ghi", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Agent connected.", html, StringComparison.Ordinal);
        Assert.Contains("Command arguments and secret values are not stored here.", html, StringComparison.Ordinal);

        using var foreign = await _client.GetAsync("/devices/agt_not_owned_activity");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Fact]
    public async Task Device_activity_project_filter_is_paginated_after_filtering()
    {
        const string project = "Paging Project";
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var agent = await db.Agents.SingleAsync(x => x.PublicId == AgentId);

            for (var i = 0; i < 55; i++)
            {
                db.AuditEvents.Add(new AuditEvent
                {
                    UserId = _ownerId,
                    AgentDeviceId = agent.Id,
                    EventType = "runtime.request",
                    Detail = JsonSerializer.Serialize(new
                    {
                        Version = 1,
                        Status = "success",
                        Level = "info",
                        Operation = $"paging-item-{i:D2}",
                        Message = "Completed successfully.",
                        DurationMs = 1.0,
                        RequestId = $"paging-{i:D2}",
                        Project = project
                    }),
                    CreatedAt = DateTimeOffset.UtcNow.AddSeconds(i)
                });
            }

            db.AuditEvents.Add(new AuditEvent
            {
                UserId = _ownerId,
                AgentDeviceId = agent.Id,
                EventType = "runtime.request",
                Detail = JsonSerializer.Serialize(new
                {
                    Version = 1,
                    Status = "success",
                    Level = "info",
                    Operation = "other-project-item",
                    Message = "Completed successfully.",
                    DurationMs = 1.0,
                    RequestId = "other-project",
                    Project = "Other Project"
                }),
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(2)
            });

            await db.SaveChangesAsync();
        }

        using var firstResponse = await _client!.GetAsync($"/devices/{AgentId}?activityProject=Paging%20Project&page=1");
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = WebUtility.HtmlDecode(await firstResponse.Content.ReadAsStringAsync());
        Assert.Contains("Showing 1–50 of 55 · Page 1 of 2", first, StringComparison.Ordinal);
        Assert.Contains("paging-item-54", first, StringComparison.Ordinal);
        Assert.Contains("paging-item-05", first, StringComparison.Ordinal);
        Assert.DoesNotContain("paging-item-04", first, StringComparison.Ordinal);
        Assert.DoesNotContain("other-project-item", first, StringComparison.Ordinal);
        Assert.Contains("activityProject=Paging%20Project", first, StringComparison.Ordinal);
        Assert.Contains("page=2", first, StringComparison.Ordinal);

        using var secondResponse = await _client.GetAsync($"/devices/{AgentId}?activityProject=Paging%20Project&page=2");
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var second = WebUtility.HtmlDecode(await secondResponse.Content.ReadAsStringAsync());
        Assert.Contains("Showing 51–55 of 55 · Page 2 of 2", second, StringComparison.Ordinal);
        Assert.Contains("paging-item-04", second, StringComparison.Ordinal);
        Assert.Contains("paging-item-00", second, StringComparison.Ordinal);
        Assert.DoesNotContain("paging-item-54", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agent_runtime_activity_retains_only_the_latest_500_events()
    {
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var agent = await db.Agents.SingleAsync(x => x.PublicId == AgentId);
            var detail = JsonSerializer.Serialize(new
            {
                Version = 1,
                Status = "success",
                Level = "info",
                Operation = "tools/call · test",
                Message = "Completed successfully.",
                DurationMs = 1.0,
                RequestId = "seed"
            });

            for (var i = 0; i < 505; i++)
            {
                db.AuditEvents.Add(new AuditEvent
                {
                    UserId = _ownerId,
                    AgentDeviceId = agent.Id,
                    EventType = "runtime.request",
                    Detail = detail,
                    CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-505 + i)
                });
            }

            await db.SaveChangesAsync();
        }

        using (var trigger = await PostInternalActivityAsync(new
        {
            agentId = AgentId,
            type = "request",
            status = "success",
            operation = "tools/call · final",
            message = "Completed successfully."
        }))
            Assert.Equal(HttpStatusCode.OK, trigger.StatusCode);

        await using var verifyScope = _factory!.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var agentId = await verifyDb.Agents.Where(x => x.PublicId == AgentId).Select(x => x.Id).SingleAsync();
        var count = await verifyDb.AuditEvents.CountAsync(x => x.AgentDeviceId == agentId && x.EventType.StartsWith("runtime."));
        Assert.Equal(500, count);
    }

    [Fact]
    public async Task Device_activity_styles_include_responsive_filter_and_log_layouts()
    {
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(ApiUrl),
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        using var response = await client.GetAsync("/portal/portal.css");
        response.EnsureSuccessStatusCode();
        var css = await response.Content.ReadAsStringAsync();

        Assert.Contains(".activity-filters", css, StringComparison.Ordinal);
        Assert.Contains(".activity-row", css, StringComparison.Ordinal);
        Assert.Contains(".activity-pagination", css, StringComparison.Ordinal);
        Assert.Contains(".activity-project-meta", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 820px)", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 520px)", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approvals_page_separates_pending_history_and_expires_stale_requests()
    {
        var pendingId = Guid.NewGuid();
        var allowedId = Guid.NewGuid();
        var deniedId = Guid.NewGuid();
        var staleId = Guid.NewGuid();

        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var agent = await db.Agents.SingleAsync(x => x.PublicId == AgentId);
            db.Approvals.AddRange(
                new ApprovalRequest
                {
                    Id = pendingId,
                    AgentDeviceId = agent.Id,
                    Capability = "shell",
                    Target = "dotnet test --filter \"VeryLongTarget\"",
                    Summary = "<script>alert('nope')</script> run the focused test suite",
                    OperationHash = "PENDING-HASH",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(4)
                },
                new ApprovalRequest
                {
                    Id = allowedId,
                    AgentDeviceId = agent.Id,
                    Capability = "filesystem",
                    Target = "C:\\Projects\\MateMCP\\README.md",
                    Summary = "Update documentation",
                    OperationHash = "ALLOWED-HASH",
                    Status = "allowed",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                    DecidedAt = DateTimeOffset.UtcNow.AddMinutes(-6)
                },
                new ApprovalRequest
                {
                    Id = deniedId,
                    AgentDeviceId = agent.Id,
                    Capability = "shell",
                    Target = "git push --force",
                    Summary = "Dangerous operation",
                    OperationHash = "DENIED-HASH",
                    Status = "denied",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-4),
                    DecidedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
                },
                new ApprovalRequest
                {
                    Id = staleId,
                    AgentDeviceId = agent.Id,
                    Capability = "browser",
                    Target = "https://example.test",
                    Summary = "Expired request",
                    OperationHash = "STALE-HASH",
                    Status = "pending",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
                });
            await db.SaveChangesAsync();
        }

        using var response = await _client!.GetAsync("/approvals");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Pending inbox", html, StringComparison.Ordinal);
        Assert.Contains("Recent history", html, StringComparison.Ordinal);
        Assert.Contains("dotnet test --filter", html, StringComparison.Ordinal);
        Assert.Contains("git push --force", html, StringComparison.Ordinal);
        Assert.Contains("Expired request", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;alert", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>alert('nope')</script>", html, StringComparison.Ordinal);
        Assert.Contains($"action=\"/approvals/{pendingId}/allow\"", html, StringComparison.Ordinal);
        Assert.Contains($"action=\"/approvals/{pendingId}/deny\"", html, StringComparison.Ordinal);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        Assert.Equal("expired", (await verifyDb.Approvals.SingleAsync(x => x.Id == staleId)).Status);
    }

    [Fact]
    public async Task Approval_decisions_are_owner_scoped_and_audited()
    {
        var ownedApprovalId = Guid.NewGuid();
        var foreignApprovalId = Guid.NewGuid();
        var foreignOwnerId = Guid.NewGuid();

        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var ownedAgent = await db.Agents.SingleAsync(x => x.PublicId == AgentId);

            db.Users.Add(new UserAccount
            {
                Id = foreignOwnerId,
                Email = "approval-owner@example.test",
                NormalizedEmail = "APPROVAL-OWNER@EXAMPLE.TEST",
                PasswordHash = "not-used"
            });
            var foreignAgent = new AgentDevice
            {
                PublicId = "agt_approval_foreign",
                OwnerId = foreignOwnerId,
                Name = "Foreign approval device",
                Platform = "windows",
                CredentialHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("approval-foreign-device")))
            };
            db.Agents.Add(foreignAgent);
            db.Approvals.AddRange(
                new ApprovalRequest
                {
                    Id = ownedApprovalId,
                    AgentDeviceId = ownedAgent.Id,
                    Capability = "shell",
                    Target = "dotnet test",
                    Summary = "Run tests",
                    OperationHash = "OWNED-APPROVAL-HASH",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(3)
                },
                new ApprovalRequest
                {
                    Id = foreignApprovalId,
                    AgentDeviceId = foreignAgent.Id,
                    Capability = "filesystem",
                    Target = "secret.txt",
                    Summary = "Foreign request",
                    OperationHash = "FOREIGN-APPROVAL-HASH",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(3)
                });
            await db.SaveChangesAsync();
        }

        using var allow = await PostPortalFormAsync(_client!, $"/approvals/{ownedApprovalId}/allow", "/approvals");
        Assert.Equal(HttpStatusCode.Redirect, allow.StatusCode);
        Assert.NotNull(allow.Headers.Location);
        Assert.Equal("/approvals?notice=allowed", allow.Headers.Location.OriginalString);

        using var foreignDeny = await PostPortalFormAsync(_client!, $"/approvals/{foreignApprovalId}/deny", "/approvals");
        Assert.Equal(HttpStatusCode.NotFound, foreignDeny.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var owned = await verifyDb.Approvals.SingleAsync(x => x.Id == ownedApprovalId);
        var foreign = await verifyDb.Approvals.SingleAsync(x => x.Id == foreignApprovalId);

        Assert.Equal("allowed", owned.Status);
        Assert.NotNull(owned.DecidedAt);
        Assert.Equal("pending", foreign.Status);
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x =>
            x.UserId == _ownerId &&
            x.AgentDeviceId == owned.AgentDeviceId &&
            x.EventType == "approval.allowed" &&
            x.Detail == "OWNED-APPROVAL-HASH"));
    }

    [Fact]
    public async Task Expired_approval_cannot_be_allowed_after_expiry()
    {
        var approvalId = Guid.NewGuid();

        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var agent = await db.Agents.SingleAsync(x => x.PublicId == AgentId);
            db.Approvals.Add(new ApprovalRequest
            {
                Id = approvalId,
                AgentDeviceId = agent.Id,
                Capability = "shell",
                Target = "delayed command",
                Summary = "This request already expired",
                OperationHash = "EXPIRED-APPROVAL-HASH",
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1)
            });
            await db.SaveChangesAsync();
        }

        using var response = await PostPortalFormAsync(_client!, $"/approvals/{approvalId}/allow", "/dashboard");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("/approvals?notice=expired", response.Headers.Location.OriginalString);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var approval = await verifyDb.Approvals.SingleAsync(x => x.Id == approvalId);
        Assert.Equal("expired", approval.Status);
        Assert.Null(approval.DecidedAt);
        Assert.False(await verifyDb.AuditEvents.AnyAsync(x => x.Detail == "EXPIRED-APPROVAL-HASH"));
    }

    [Fact]
    public async Task Approval_history_filter_limits_completed_rows_by_status()
    {
        var allowedTarget = "allowed-target-" + Guid.NewGuid().ToString("N");
        var deniedTarget = "denied-target-" + Guid.NewGuid().ToString("N");

        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            var agent = await db.Agents.SingleAsync(x => x.PublicId == AgentId);
            db.Approvals.AddRange(
                new ApprovalRequest
                {
                    AgentDeviceId = agent.Id,
                    Capability = "shell",
                    Target = allowedTarget,
                    Summary = "Allowed history row",
                    OperationHash = "FILTER-ALLOWED",
                    Status = "allowed",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                    DecidedAt = DateTimeOffset.UtcNow.AddMinutes(-3)
                },
                new ApprovalRequest
                {
                    AgentDeviceId = agent.Id,
                    Capability = "shell",
                    Target = deniedTarget,
                    Summary = "Denied history row",
                    OperationHash = "FILTER-DENIED",
                    Status = "denied",
                    ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                    DecidedAt = DateTimeOffset.UtcNow.AddMinutes(-3)
                });
            await db.SaveChangesAsync();
        }

        using var response = await _client!.GetAsync("/approvals?filter=allowed");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains(allowedTarget, html, StringComparison.Ordinal);
        Assert.DoesNotContain(deniedTarget, html, StringComparison.Ordinal);
        Assert.Contains("aria-current=\"page\">Allowed</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Regular_user_is_forbidden_from_administration_routes()
    {
        using var response = await _client!.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var detail = await _client.GetAsync($"/admin/users/{_ownerId}");
        Assert.Equal(HttpStatusCode.Forbidden, detail.StatusCode);
    }

    [Fact]
    public async Task Administrator_can_search_users_without_rendering_credentials()
    {
        using var adminClient = await CreateLoggedInClientAsync(AdminEmail, AdminPassword);

        using var response = await adminClient.GetAsync("/admin?q=oauth-refresh");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Administration", html, StringComparison.Ordinal);
        Assert.Contains(Email, html, StringComparison.Ordinal);
        Assert.Contains("/admin/users/", html, StringComparison.Ordinal);
        Assert.Contains("Admin", html, StringComparison.Ordinal);
        Assert.DoesNotContain("oauth-refresh-password", html, StringComparison.Ordinal);
        Assert.DoesNotContain("agent-credential", html, StringComparison.Ordinal);

        string passwordHash;
        string credentialHash;
        await using (var scope = _factory!.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            passwordHash = (await db.Users.AsNoTracking().SingleAsync(x => x.Id == _ownerId)).PasswordHash;
            credentialHash = (await db.Agents.AsNoTracking().SingleAsync(x => x.PublicId == AgentId)).CredentialHash;
        }

        Assert.DoesNotContain(passwordHash, html, StringComparison.Ordinal);
        Assert.DoesNotContain(credentialHash, html, StringComparison.Ordinal);

        using var detail = await adminClient.GetAsync($"/admin/users/{_ownerId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailHtml = await detail.Content.ReadAsStringAsync();
        Assert.Contains(AgentId, detailHtml, StringComparison.Ordinal);
        Assert.Contains("Disable account", detailHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(credentialHash, detailHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabling_user_invalidates_existing_session_and_agent_access_until_reenabled()
    {
        using var adminClient = await CreateLoggedInClientAsync(AdminEmail, AdminPassword);

        using var confirmation = await adminClient.GetAsync($"/admin/users/{_ownerId}/disable");
        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        var confirmationHtml = await confirmation.Content.ReadAsStringAsync();
        Assert.Contains("Disable user account", confirmationHtml, StringComparison.Ordinal);
        Assert.Contains($"action=\"/admin/users/{_ownerId}/disable\"", confirmationHtml, StringComparison.Ordinal);

        using var disable = await PostPortalFormAsync(adminClient, $"/admin/users/{_ownerId}/disable", $"/admin/users/{_ownerId}/disable");
        Assert.Equal(HttpStatusCode.Redirect, disable.StatusCode);

        using var staleSession = await _client!.GetAsync("/dashboard");
        Assert.Equal(HttpStatusCode.Redirect, staleSession.StatusCode);
        Assert.NotNull(staleSession.Headers.Location);
        Assert.Equal("/login", staleSession.Headers.Location.AbsolutePath);

        using var agentAuth = new HttpRequestMessage(HttpMethod.Post, "/internal/agents/authenticate")
        {
            Content = JsonContent.Create(new { agentId = AgentId, credential = "agent-credential" })
        };
        agentAuth.Headers.Add("X-MateMCP-Internal-Key", InternalKey);
        using var agentAuthResponse = await adminClient.SendAsync(agentAuth);
        Assert.Equal(HttpStatusCode.Unauthorized, agentAuthResponse.StatusCode);

        using var createApproval = new HttpRequestMessage(HttpMethod.Post, $"/api/agents/{AgentId}/approvals")
        {
            Content = JsonContent.Create(new { capability = "shell", target = "dotnet test", summary = "disabled owner", expiresIn = 120 })
        };
        createApproval.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "agent-credential");
        using var createApprovalResponse = await adminClient.SendAsync(createApproval);
        Assert.Equal(HttpStatusCode.Unauthorized, createApprovalResponse.StatusCode);

        using var deviceListRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/agents/{AgentId}/devices");
        deviceListRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "agent-credential");
        using var deviceListResponse = await adminClient.SendAsync(deviceListRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, deviceListResponse.StatusCode);

        using var enable = await PostPortalFormAsync(adminClient, $"/admin/users/{_ownerId}/enable", $"/admin/users/{_ownerId}");
        Assert.Equal(HttpStatusCode.Redirect, enable.StatusCode);

        using var freshUserClient = await CreateLoggedInClientAsync(Email, Password);
        using var dashboard = await freshUserClient.GetAsync("/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);

        using var restoredAuth = new HttpRequestMessage(HttpMethod.Post, "/internal/agents/authenticate")
        {
            Content = JsonContent.Create(new { agentId = AgentId, credential = "agent-credential" })
        };
        restoredAuth.Headers.Add("X-MateMCP-Internal-Key", InternalKey);
        using var restoredAuthResponse = await adminClient.SendAsync(restoredAuth);
        Assert.Equal(HttpStatusCode.OK, restoredAuthResponse.StatusCode);

        await using var verifyScope = _factory!.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        Assert.False((await verifyDb.Users.SingleAsync(x => x.Id == _ownerId)).IsDisabled);
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x => x.UserId == _adminId && x.EventType == "admin.user.disabled" && x.Detail == _ownerId.ToString()));
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x => x.UserId == _adminId && x.EventType == "admin.user.enabled" && x.Detail == _ownerId.ToString()));
    }

    [Fact]
    public async Task Administrator_cannot_disable_self_and_can_revoke_a_users_device_with_audit()
    {
        using var adminClient = await CreateLoggedInClientAsync(AdminEmail, AdminPassword);

        using var selfConfirmation = await adminClient.GetAsync($"/admin/users/{_adminId}/disable");
        Assert.Equal(HttpStatusCode.Conflict, selfConfirmation.StatusCode);
        var selfHtml = await selfConfirmation.Content.ReadAsStringAsync();
        Assert.Contains("cannot disable your own administrator account", selfHtml, StringComparison.OrdinalIgnoreCase);

        using var selfDisable = await PostPortalFormAsync(adminClient, $"/admin/users/{_adminId}/disable", $"/admin/users/{_adminId}");
        Assert.Equal(HttpStatusCode.BadRequest, selfDisable.StatusCode);

        using var revokeConfirmation = await adminClient.GetAsync($"/admin/devices/{AgentId}/revoke");
        Assert.Equal(HttpStatusCode.OK, revokeConfirmation.StatusCode);
        var revokeHtml = await revokeConfirmation.Content.ReadAsStringAsync();
        Assert.Contains("Revoke user device", revokeHtml, StringComparison.Ordinal);
        Assert.Contains(Email, revokeHtml, StringComparison.Ordinal);
        Assert.Contains($"action=\"/admin/devices/{AgentId}/revoke\"", revokeHtml, StringComparison.Ordinal);

        using var revoke = await PostPortalFormAsync(adminClient, $"/admin/devices/{AgentId}/revoke", $"/admin/devices/{AgentId}/revoke");
        Assert.Equal(HttpStatusCode.Redirect, revoke.StatusCode);

        await using var verifyScope = _factory!.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var agent = await verifyDb.Agents.SingleAsync(x => x.PublicId == AgentId);
        Assert.True(agent.IsRevoked);
        Assert.True(await verifyDb.AuditEvents.AnyAsync(x =>
            x.UserId == _adminId &&
            x.AgentDeviceId == agent.Id &&
            x.EventType == "admin.agent.revoked" &&
            x.Detail == _ownerId.ToString()));
    }

    [Fact]
    public async Task Administrative_mutations_reject_missing_antiforgery_token()
    {
        using var adminClient = await CreateLoggedInClientAsync(AdminEmail, AdminPassword);
        using var response = await adminClient.PostAsync($"/admin/users/{_ownerId}/disable", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var verifyScope = _factory!.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        Assert.False((await verifyDb.Users.SingleAsync(x => x.Id == _ownerId)).IsDisabled);
    }

    [Fact]
    public async Task Admin_styles_keep_responsive_operational_layout_contracts()
    {
        using var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(ApiUrl),
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        using var response = await client.GetAsync("/portal/portal.css");
        response.EnsureSuccessStatusCode();
        var css = await response.Content.ReadAsStringAsync();

        Assert.Contains(".admin-user-row", css, StringComparison.Ordinal);
        Assert.Contains(".admin-account-grid", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 980px)", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 620px)", css, StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> PostPortalFormAsync(
        HttpClient client,
        string postPath,
        string tokenPage,
        Dictionary<string, string>? fields = null)
    {
        using var page = await client.GetAsync(tokenPage);
        Assert.True(
            page.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
            $"Expected an HTML form page for anti-forgery token but received {(int)page.StatusCode} from {tokenPage}.");
        var html = await page.Content.ReadAsStringAsync();
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"", RegexOptions.CultureInvariant);
        Assert.True(match.Success, $"Anti-forgery token was not rendered by {tokenPage}.");

        var payload = fields is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(fields, StringComparer.Ordinal);
        payload["__RequestVerificationToken"] = WebUtility.HtmlDecode(match.Groups[1].Value);

        return await client.PostAsync(postPath, new FormUrlEncodedContent(payload));
    }

    private async Task<HttpClient> CreateLoggedInClientAsync(string email, string password)
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(ApiUrl),
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        using var login = await PostPortalFormAsync(client, "/login", "/login", new Dictionary<string, string>
        {
            ["email"] = email,
            ["password"] = password,
            ["returnUrl"] = "/dashboard"
        });
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        return client;
    }

    private async Task<HttpResponseMessage> PostInternalActivityAsync(object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/agents/activity")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("X-MateMCP-Internal-Key", InternalKey);
        return await _client!.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostInternalAuthorizeAsync(string[] scopes)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/agents/authorize")
        {
            Content = JsonContent.Create(new { agentId = AgentId, userId = _ownerId.ToString(), scopes })
        };
        request.Headers.Add("X-MateMCP-Internal-Key", InternalKey);
        return await _client!.SendAsync(request);
    }

    private static void AssertScopes(JsonElement response, string[] required, string[] forbidden)
    {
        var scope = response.TryGetProperty("scope", out var property) ? property.GetString() ?? string.Empty : string.Empty;
        var scopes = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        foreach (var item in required) Assert.Contains(item, scopes);
        foreach (var item in forbidden) Assert.DoesNotContain(item, scopes);
    }

    private void AssertAccessTokenBinding(string token, string expectedResource)
    {
        var segments = token.Split('.');
        Assert.True(segments.Length >= 2);
        using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(segments[1]));
        var root = payload.RootElement;
        Assert.Equal(_ownerId.ToString(), root.GetProperty("sub").GetString());
        Assert.Equal(AgentId, root.GetProperty("agent_id").GetString());

        var audiences = root.GetProperty("aud").ValueKind == JsonValueKind.Array
            ? root.GetProperty("aud").EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).Cast<string>().ToArray()
            : [root.GetProperty("aud").GetString()!];
        Assert.Contains(expectedResource, audiences);
    }

    private void SetEnvironment(string key, string value)
    {
        _originalEnvironment[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var (key, value) in _originalEnvironment) Environment.SetEnvironmentVariable(key, value);
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }
}