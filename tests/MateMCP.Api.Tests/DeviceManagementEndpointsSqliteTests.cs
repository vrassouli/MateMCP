using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MateMCP.Api.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

public sealed class DeviceManagementEndpointsSqliteTests
{
    [Fact]
    public async Task GetDevices_OrdersSameNameByCreatedAt_OnSqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<ControlPlaneDbContext>(options => options.UseSqlite(connection));

        await using var app = builder.Build();
        DeviceManagementEndpoints.Map(app, "https://relay.test");
        await app.StartAsync();

        var ownerId = Guid.NewGuid();
        const string actorToken = "device-regression-test-token";
        var firstCreatedAt = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var secondCreatedAt = firstCreatedAt.AddHours(1);
        var thirdCreatedAt = firstCreatedAt.AddHours(2);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
            await db.Database.EnsureCreatedAsync();

            db.Users.Add(new UserAccount
            {
                Id = ownerId,
                Email = "sqlite-device-test@example.test",
                NormalizedEmail = "SQLITE-DEVICE-TEST@EXAMPLE.TEST",
                PasswordHash = "not-used"
            });

            db.Agents.AddRange(
                Device("agt_second", secondCreatedAt, actorToken),
                Device("agt_third", thirdCreatedAt, "third-token"),
                Device("agt_first", firstCreatedAt, "first-token"));

            await db.SaveChangesAsync();
        }

        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", actorToken);

        using var response = await client.GetAsync("/api/agents/agt_second/devices");
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var ids = json.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("id").GetString())
            .ToArray();

        Assert.Equal(new[] { "agt_first", "agt_second", "agt_third" }, ids);

        AgentDevice Device(string publicId, DateTimeOffset createdAt, string token)
            => new()
            {
                PublicId = publicId,
                OwnerId = ownerId,
                Name = "Same device name",
                Platform = "test",
                CredentialHash = Hash(token),
                CreatedAt = createdAt
            };
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
