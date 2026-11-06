using System.Net;
using System.Text.RegularExpressions;
using MateMCP.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[Collection("API integration serial")]
public sealed class LocalRegistrationTests
{
    [Fact]
    public async Task Registration_RequiresMatchingConfirmation_AndStoresOnlyThePasswordHash()
    {
        var root = Path.Combine(Path.GetTempPath(), "matemcp-registration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var environmentKeys = new[]
        {
            "MateMCP__PublicUrl",
            "MateMCP__RelayUrl",
            "MateMCP__DatabaseProvider",
            "MateMCP__ConnectionStringBase64",
            "MateMCP__InternalApiKey",
            "MateMCP__KeyPath",
            "MateMCP__BootstrapAdminEmail",
            "MateMCP__BootstrapAdminPassword",
            "ConnectionStrings__MateMCP",
            "MateMCP__ExternalAuth__Providers__Google__Enabled",
            "MateMCP__ExternalAuth__Providers__Google__ClientId",
            "MateMCP__ExternalAuth__Providers__Google__ClientSecret",
            "MateMCP__ExternalAuth__Providers__GitHub__Enabled",
            "MateMCP__ExternalAuth__Providers__GitHub__ClientId",
            "MateMCP__ExternalAuth__Providers__GitHub__ClientSecret",
            "MateMCP__ExternalAuth__Providers__Microsoft__Enabled",
            "MateMCP__ExternalAuth__Providers__Microsoft__ClientId",
            "MateMCP__ExternalAuth__Providers__Microsoft__ClientSecret",
            "MateMCP__ExternalAuth__Providers__Apple__Enabled",
            "MateMCP__ExternalAuth__Providers__Apple__ClientId",
            "MateMCP__ExternalAuth__Providers__Apple__ClientSecret"
        };
        var originalEnvironment = environmentKeys.ToDictionary(
            key => key,
            key => Environment.GetEnvironmentVariable(key));

        try
        {
            Environment.SetEnvironmentVariable("MateMCP__PublicUrl", "https://api.registration.test");
            Environment.SetEnvironmentVariable("MateMCP__RelayUrl", "https://relay.registration.test");
            Environment.SetEnvironmentVariable("MateMCP__DatabaseProvider", "sqlite");
            Environment.SetEnvironmentVariable("MateMCP__ConnectionStringBase64", "");
            Environment.SetEnvironmentVariable("MateMCP__InternalApiKey", "registration-test-internal-key");
            Environment.SetEnvironmentVariable("MateMCP__KeyPath", Path.Combine(root, "keys"));
            Environment.SetEnvironmentVariable("MateMCP__BootstrapAdminEmail", "");
            Environment.SetEnvironmentVariable("MateMCP__BootstrapAdminPassword", "");
            Environment.SetEnvironmentVariable("ConnectionStrings__MateMCP", $"Data Source={Path.Combine(root, "matemcp.db")}");

            foreach (var key in environmentKeys.Where(key => key.Contains("ExternalAuth", StringComparison.Ordinal)))
                Environment.SetEnvironmentVariable(key, key.EndsWith("__Enabled", StringComparison.Ordinal) ? "false" : "");

            using var factory = new WebApplicationFactory<Program>();
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://api.registration.test"),
                AllowAutoRedirect = false,
                HandleCookies = true
            });

            using (var login = await client.GetAsync("/login"))
            {
                Assert.Equal(HttpStatusCode.OK, login.StatusCode);
                var loginHtml = await login.Content.ReadAsStringAsync();
                Assert.DoesNotContain("Confirm password", loginHtml, StringComparison.Ordinal);
            }

            using (var registration = await client.GetAsync("/register"))
            {
                Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
                var registrationHtml = await registration.Content.ReadAsStringAsync();
                Assert.Contains("Confirm password", registrationHtml, StringComparison.Ordinal);
                Assert.Contains("confirmPassword", registrationHtml, StringComparison.Ordinal);
                Assert.Contains("aria-live", registrationHtml, StringComparison.Ordinal);
                Assert.Contains("/portal/registration-validation.js", registrationHtml, StringComparison.Ordinal);
            }

            using (var validationScript = await client.GetAsync("/portal/registration-validation.js"))
            {
                Assert.Equal(HttpStatusCode.OK, validationScript.StatusCode);
                var script = await validationScript.Content.ReadAsStringAsync();
                Assert.Contains("Passwords do not match.", script, StringComparison.Ordinal);
            }

            async Task<string> GetRequestTokenAsync()
            {
                using var page = await client.GetAsync("/register");
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                var html = await page.Content.ReadAsStringAsync();
                var match = Regex.Match(html, @"name=""__RequestVerificationToken"" value=""([^""]+)""");
                Assert.True(match.Success, "The registration page should contain an antiforgery token.");
                return WebUtility.HtmlDecode(match.Groups[1].Value);
            }

            async Task<HttpResponseMessage> SubmitRegistrationAsync(string email, string password, string? confirmation)
            {
                var values = new Dictionary<string, string>
                {
                    ["email"] = email,
                    ["password"] = password,
                    ["returnUrl"] = "/dashboard",
                    ["__RequestVerificationToken"] = await GetRequestTokenAsync()
                };
                if (confirmation is not null)
                    values["confirmPassword"] = confirmation;

                using var form = new FormUrlEncodedContent(values);
                return await client.PostAsync("/register", form);
            }

            using (var mismatch = await SubmitRegistrationAsync(
                       "mismatch@example.test",
                       "a-valid-password",
                       "a-different-password"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
                var html = await mismatch.Content.ReadAsStringAsync();
                Assert.Contains("Passwords do not match.", html, StringComparison.Ordinal);
                Assert.Contains("data-server-error", html, StringComparison.Ordinal);
            }

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
                Assert.Empty(await db.Users.ToListAsync());
            }

            using (var missing = await SubmitRegistrationAsync(
                       "missing@example.test",
                       "a-valid-password",
                       confirmation: null))
            {
                Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
                var html = await missing.Content.ReadAsStringAsync();
                Assert.Contains("Please confirm your password.", html, StringComparison.Ordinal);
            }

            using (var success = await SubmitRegistrationAsync(
                       "matched@example.test",
                       "a-valid-password",
                       "a-valid-password"))
            {
                Assert.Equal(HttpStatusCode.Redirect, success.StatusCode);
                Assert.Equal("/dashboard", success.Headers.Location?.OriginalString);
            }

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
                var user = await db.Users.SingleAsync(x => x.NormalizedEmail == "MATCHED@EXAMPLE.TEST");
                var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<UserAccount>>();

                Assert.Equal(
                    PasswordVerificationResult.Success,
                    hasher.VerifyHashedPassword(user, user.PasswordHash, "a-valid-password"));
                Assert.Single(await db.Users.ToListAsync());
            }
        }
        finally
        {
            foreach (var pair in originalEnvironment)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);

            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}