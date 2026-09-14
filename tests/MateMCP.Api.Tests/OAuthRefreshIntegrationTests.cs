using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MateMCP.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

public sealed class OAuthRefreshIntegrationTests : IAsyncLifetime
{
    private const string ApiUrl = "https://api.test";
    private const string RelayUrl = "https://relay.test";
    private const string AgentId = "agt_refresh_test";
    private const string InternalKey = "oauth-refresh-integration-internal-key";
    private const string Email = "oauth-refresh@example.test";
    private const string Password = "oauth-refresh-password";
    private const string RedirectUri = "https://client.test/callback";

    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "matemcp-oauth-" + Guid.NewGuid().ToString("N"));
    private readonly Guid _ownerId = Guid.NewGuid();
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempRoot);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MateMCP:PublicUrl"] = ApiUrl,
                    ["MateMCP:RelayUrl"] = RelayUrl,
                    ["MateMCP:DatabaseProvider"] = "sqlite",
                    ["ConnectionStrings:MateMCP"] = $"Data Source={Path.Combine(_tempRoot, "matemcp.db")}",
                    ["MateMCP:InternalApiKey"] = InternalKey,
                    ["MateMCP:KeyPath"] = Path.Combine(_tempRoot, "keys")
                });
            });
        });

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(ApiUrl),
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        using (var health = await _client.GetAsync("/health"))
            health.EnsureSuccessStatusCode();

        await using (var scope = _factory.Services.CreateAsyncScope())
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
            db.Users.Add(user);
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

        using var login = await _client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["email"] = Email,
            ["password"] = Password,
            ["returnUrl"] = "/dashboard"
        }));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
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

        using var token = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId!,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier
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

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }
}
