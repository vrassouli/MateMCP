using MateMCP.Agent.Security;

namespace MateMCP.Agent.Tests;

public sealed class UserSecretStorePlatformTests
{
    [Fact]
    public async Task Save_resolve_list_and_delete_round_trip_in_platform_credential_store()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(Path.GetTempPath(), "matemcp-tests", Guid.NewGuid().ToString("N"));
        var store = new UserSecretStore(Path.Combine(directory, "secrets.json"));
        var name = $"platform-test-{Guid.NewGuid():N}";
        var value = $"secret-{Guid.NewGuid():N}";

        try
        {
            await store.SaveAsync(
                name,
                value,
                "Platform credential store integration test",
                CredentialKind.Password,
                [UserSecretInfo.ShellSessionSendSecretTool],
                CancellationToken.None);

            Assert.Equal(value, await store.ResolveAsync(name, CancellationToken.None));
            var saved = Assert.Single(await store.ListAsync(CancellationToken.None));
            Assert.Equal(name, saved.Name);
            Assert.True(saved.IsAllowedForTool(UserSecretInfo.ShellSessionSendSecretTool));
        }
        finally
        {
            await store.DeleteAsync(name, CancellationToken.None);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
