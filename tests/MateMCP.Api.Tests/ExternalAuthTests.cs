using System.Net;
using MateMCP.Api.Data;
using MateMCP.Api.ExternalAuth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

[Collection("API integration serial")]
public sealed class ExternalAuthTests
{
    [Fact]
    public void Catalog_LoadsEnabledKnownProviders_AndRejectsIncompleteConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["MateMCP:ExternalAuth:Providers:Google:Enabled"] = "true",
            ["MateMCP:ExternalAuth:Providers:Google:ClientId"] = "google-client",
            ["MateMCP:ExternalAuth:Providers:Google:ClientSecret"] = "google-secret",
            ["MateMCP:ExternalAuth:Providers:Microsoft:Enabled"] = "true",
            ["MateMCP:ExternalAuth:Providers:Microsoft:ClientId"] = "microsoft-client",
            ["MateMCP:ExternalAuth:Providers:Microsoft:ClientSecret"] = "microsoft-secret",
            ["MateMCP:ExternalAuth:Providers:GitHub:Enabled"] = "true",
            ["MateMCP:ExternalAuth:Providers:GitHub:ClientId"] = "github-client",
            ["MateMCP:ExternalAuth:Providers:GitHub:ClientSecret"] = "github-secret",
            ["MateMCP:ExternalAuth:Providers:Apple:Enabled"] = "true",
            ["MateMCP:ExternalAuth:Providers:Apple:ClientId"] = "apple-client",
            ["MateMCP:ExternalAuth:Providers:Apple:ClientSecret"] = "apple-secret"
        };

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var catalog = ExternalAuthCatalog.FromConfiguration(configuration);

        Assert.Equal(["google", "microsoft", "github", "apple"], catalog.Providers.Select(x => x.Id).ToArray());
        Assert.All(catalog.Providers, provider => Assert.StartsWith("/auth/callback/", provider.CallbackPath, StringComparison.Ordinal));

        values.Remove("MateMCP:ExternalAuth:Providers:Apple:ClientSecret");
        configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.Throws<InvalidOperationException>(() => ExternalAuthCatalog.FromConfiguration(configuration));
    }

    [Fact]
    public async Task Completion_CreatesNewAccount_OnlyForVerifiedEmail()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var service = fixture.CreateService();

        var rejected = await service.CompleteAsync(
            "google",
            "google-unverified",
            "new@example.test",
            emailVerified: false,
            explicitLinkUserId: null);

        Assert.False(rejected.Succeeded);
        Assert.Empty(await fixture.Db.Users.ToListAsync());

        var created = await service.CompleteAsync(
            "google",
            "google-verified",
            "new@example.test",
            emailVerified: true,
            explicitLinkUserId: null);

        Assert.True(created.Succeeded);
        Assert.True(created.Created);
        Assert.Equal("new@example.test", created.User!.Email);
        Assert.Single(await fixture.Db.ExternalLogins.ToListAsync());
        Assert.Contains(await fixture.Db.AuditEvents.ToListAsync(), x => x.EventType == "external_account_created");
    }

    [Fact]
    public async Task Completion_DoesNotAutoLinkAnExistingEmail_ButExplicitLinkWorks()
    {
        await using var fixture = await DbFixture.CreateAsync();
        var user = new UserAccount
        {
            Email = "existing@example.test",
            NormalizedEmail = "EXISTING@EXAMPLE.TEST",
            PasswordHash = "pending"
        };
        user.PasswordHash = fixture.Hasher.HashPassword(user, "local-password");
        fixture.Db.Users.Add(user);
        await fixture.Db.SaveChangesAsync();

        var service = fixture.CreateService();
        var automatic = await service.CompleteAsync(
            "github",
            "github-123",
            user.Email,
            emailVerified: true,
            explicitLinkUserId: null);

        Assert.False(automatic.Succeeded);
        Assert.Contains("Sign in with your password first", automatic.Error, StringComparison.Ordinal);
        Assert.Empty(await fixture.Db.ExternalLogins.ToListAsync());

        var linked = await service.CompleteAsync(
            "github",
            "github-123",
            user.Email,
            emailVerified: true,
            explicitLinkUserId: user.Id);

        Assert.True(linked.Succeeded);
        Assert.True(linked.Linked);
        var login = Assert.Single(await fixture.Db.ExternalLogins.ToListAsync());
        Assert.Equal(user.Id, login.UserAccountId);
        Assert.Contains(await fixture.Db.AuditEvents.ToListAsync(), x => x.EventType == "external_login_linked");
    }

    [Fact]
    public async Task Completion_RejectsDisabledLinkedAccount_AndCrossAccountRelink()
    {
        await using var fixture = await DbFixture.CreateAsync();

        var first = new UserAccount
        {
            Email = "first@example.test",
            NormalizedEmail = "FIRST@EXAMPLE.TEST",
            PasswordHash = "pending"
        };
        first.PasswordHash = fixture.Hasher.HashPassword(first, "first-password");

        var second = new UserAccount
        {
            Email = "second@example.test",
            NormalizedEmail = "SECOND@EXAMPLE.TEST",
            PasswordHash = "pending"
        };
        second.PasswordHash = fixture.Hasher.HashPassword(second, "second-password");

        fixture.Db.Users.AddRange(first, second);
        await fixture.Db.SaveChangesAsync();

        var service = fixture.CreateService();
        var linked = await service.CompleteAsync(
            "google",
            "google-existing",
            first.Email,
            emailVerified: true,
            explicitLinkUserId: first.Id);
        Assert.True(linked.Succeeded);

        var crossAccount = await service.CompleteAsync(
            "google",
            "google-existing",
            first.Email,
            emailVerified: true,
            explicitLinkUserId: second.Id);
        Assert.False(crossAccount.Succeeded);
        Assert.Contains("different MateMCP account", crossAccount.Error!, StringComparison.Ordinal);

        first.IsDisabled = true;
        await fixture.Db.SaveChangesAsync();

        var disabled = await service.CompleteAsync(
            "google",
            "google-existing",
            first.Email,
            emailVerified: true,
            explicitLinkUserId: null);
        Assert.False(disabled.Succeeded);
        Assert.Contains("disabled", disabled.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SchemaUpgrade_RecreatesExternalLoginTable_ForExistingSqliteDatabase()
    {
        await using var fixture = await DbFixture.CreateAsync();
        await fixture.Db.Database.ExecuteSqlRawAsync("DROP TABLE \"ExternalLogins\";");

        await DatabaseSchemaUpgrade.EnsureExternalLoginsAsync(fixture.Db, "sqlite");
        await DatabaseSchemaUpgrade.EnsureExternalLoginsAsync(fixture.Db, "sqlite");

        await using var command = fixture.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ExternalLogins';";
        var count = Convert.ToInt64(await command.ExecuteScalarAsync());
        Assert.Equal(1L, count);
    }

    [Fact]
    public async Task Portal_ShowsOnlyEnabledProvider_AndGitHubChallengeStartsOAuthRedirect()
    {
        var root = Path.Combine(Path.GetTempPath(), "matemcp-external-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var environment = new Dictionary<string, string?>
        {
            ["MateMCP__PublicUrl"] = Environment.GetEnvironmentVariable("MateMCP__PublicUrl"),
            ["MateMCP__RelayUrl"] = Environment.GetEnvironmentVariable("MateMCP__RelayUrl"),
            ["MateMCP__DatabaseProvider"] = Environment.GetEnvironmentVariable("MateMCP__DatabaseProvider"),
            ["ConnectionStrings__MateMCP"] = Environment.GetEnvironmentVariable("ConnectionStrings__MateMCP"),
            ["MateMCP__InternalApiKey"] = Environment.GetEnvironmentVariable("MateMCP__InternalApiKey"),
            ["MateMCP__KeyPath"] = Environment.GetEnvironmentVariable("MateMCP__KeyPath"),
            ["MateMCP__ExternalAuth__Providers__GitHub__Enabled"] = Environment.GetEnvironmentVariable("MateMCP__ExternalAuth__Providers__GitHub__Enabled"),
            ["MateMCP__ExternalAuth__Providers__GitHub__ClientId"] = Environment.GetEnvironmentVariable("MateMCP__ExternalAuth__Providers__GitHub__ClientId"),
            ["MateMCP__ExternalAuth__Providers__GitHub__ClientSecret"] = Environment.GetEnvironmentVariable("MateMCP__ExternalAuth__Providers__GitHub__ClientSecret")
        };

        try
        {
            Environment.SetEnvironmentVariable("MateMCP__PublicUrl", "https://api.external.test");
            Environment.SetEnvironmentVariable("MateMCP__RelayUrl", "https://relay.external.test");
            Environment.SetEnvironmentVariable("MateMCP__DatabaseProvider", "sqlite");
            Environment.SetEnvironmentVariable("ConnectionStrings__MateMCP", $"Data Source={Path.Combine(root, "matemcp.db")}");
            Environment.SetEnvironmentVariable("MateMCP__InternalApiKey", "external-auth-test-internal-key");
            Environment.SetEnvironmentVariable("MateMCP__KeyPath", Path.Combine(root, "keys"));
            Environment.SetEnvironmentVariable("MateMCP__ExternalAuth__Providers__GitHub__Enabled", "true");
            Environment.SetEnvironmentVariable("MateMCP__ExternalAuth__Providers__GitHub__ClientId", "github-client");
            Environment.SetEnvironmentVariable("MateMCP__ExternalAuth__Providers__GitHub__ClientSecret", "github-secret");

            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://api.external.test"),
                AllowAutoRedirect = false,
                HandleCookies = true
            });

            using var login = await client.GetAsync("/login");
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var html = await login.Content.ReadAsStringAsync();
            Assert.Contains("Continue with GitHub", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Continue with Google", html, StringComparison.Ordinal);

            using var register = await client.GetAsync("/register");
            Assert.Equal(HttpStatusCode.OK, register.StatusCode);
            var registerHtml = await register.Content.ReadAsStringAsync();
            Assert.Contains("Continue with GitHub", registerHtml, StringComparison.Ordinal);
            Assert.DoesNotContain("Continue with Google", registerHtml, StringComparison.Ordinal);

            using var disabled = await client.GetAsync("/auth/external/google");
            Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);

            using var challenge = await client.GetAsync("/auth/external/github?returnUrl=%2Fdashboard");
            Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
            Assert.NotNull(challenge.Headers.Location);
            Assert.Equal("github.com", challenge.Headers.Location!.Host);
            Assert.Equal("/login/oauth/authorize", challenge.Headers.Location.AbsolutePath);
        }
        finally
        {
            foreach (var pair in environment)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);

            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private sealed class DbFixture : IAsyncDisposable
    {
        private DbFixture(SqliteConnection connection, ControlPlaneDbContext db)
        {
            Connection = connection;
            Db = db;
            Hasher = new PasswordHasher<UserAccount>();
        }

        public SqliteConnection Connection { get; }
        public ControlPlaneDbContext Db { get; }
        public PasswordHasher<UserAccount> Hasher { get; }

        public ExternalAuthAccountService CreateService() => new(Db, Hasher);

        public static async Task<DbFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
                .UseSqlite(connection)
                .Options;
            var db = new ControlPlaneDbContext(options);
            await db.Database.EnsureCreatedAsync();
            return new DbFixture(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
