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
        var indexPath = Path.Combine(directory, "secrets.json");
        var store = new UserSecretStore(indexPath);
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

            // Recreate the store as a new Agent process/update would. Both the
            // metadata index and the platform credential value must still resolve.
            var reopened = new UserSecretStore(indexPath);
            Assert.Equal(value, await reopened.ResolveAsync(name, CancellationToken.None));
            var persisted = Assert.Single(await reopened.ListAsync(CancellationToken.None));
            Assert.Equal(name, persisted.Name);
            Assert.Equal("Platform credential store integration test", persisted.Description);
            Assert.True(persisted.IsAllowedForTool(UserSecretInfo.ShellSessionSendSecretTool));
        }
        finally
        {
            await store.DeleteAsync(name, CancellationToken.None);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Metadata_update_preserves_the_platform_secret_value()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(Path.GetTempPath(), "matemcp-tests", Guid.NewGuid().ToString("N"));
        var indexPath = Path.Combine(directory, "secrets.json");
        var store = new UserSecretStore(indexPath);
        var name = $"metadata-test-{Guid.NewGuid():N}";
        var value = $"secret-{Guid.NewGuid():N}";

        try
        {
            await store.SaveAsync(
                name,
                value,
                "Before",
                CredentialKind.Password,
                [UserSecretInfo.ShellSessionSendSecretTool],
                CancellationToken.None);

            var updated = await store.UpdateMetadataAsync(
                name,
                "After",
                CredentialKind.Generic,
                [UserSecretInfo.UiFillSecretTool, UserSecretInfo.BrowserFillSecretTool],
                CancellationToken.None);

            Assert.NotNull(updated);
            Assert.Equal("After", updated!.Description);
            Assert.Equal(CredentialKind.Generic, updated.Kind);
            Assert.True(updated.IsAllowedForTool(UserSecretInfo.UiFillSecretTool));
            Assert.True(updated.IsAllowedForTool(UserSecretInfo.BrowserFillSecretTool));
            Assert.Equal(value, await store.ResolveAsync(name, CancellationToken.None));
        }
        finally
        {
            await store.DeleteAsync(name, CancellationToken.None);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Secret_metadata_update_path_never_reads_or_rewrites_plaintext()
    {
        var root = FindRepositoryRoot();
        var store = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Security", "UserSecretStore.cs"));
        var program = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Program.cs"));

        var methodStart = store.IndexOf("public async Task<UserSecretInfo?> UpdateMetadataAsync", StringComparison.Ordinal);
        var methodEnd = store.IndexOf("public async Task<string?> ResolveAsync", methodStart, StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart);
        var method = store[methodStart..methodEnd];

        Assert.DoesNotContain("ReadPlatformSecretAsync", method, StringComparison.Ordinal);
        Assert.DoesNotContain("SavePlatformSecretAsync", method, StringComparison.Ordinal);
        Assert.Contains("app.MapPut(\"/secrets/{name}\"", program, StringComparison.Ordinal);
        Assert.Contains("SecretMetadataUpdate(string? Description, CredentialKind Kind", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Desktop_installers_preserve_the_user_secret_store_identity()
    {
        var root = FindRepositoryRoot();
        var windows = File.ReadAllText(Path.Combine(root, "scripts", "install-windows.ps1"));
        var mac = File.ReadAllText(Path.Combine(root, "scripts", "install-macos.sh"));
        var macMode = File.ReadAllText(Path.Combine(root, "scripts", "configure-agent-mode-macos.sh"));
        var store = File.ReadAllText(Path.Combine(root, "src", "MateMCP.Agent", "Security", "UserSecretStore.cs"));

        // Windows payload replacement is LOCALAPPDATA-only; durable Agent state uses
        // APPDATA and secret values remain in Windows Credential Manager.
        Assert.Contains("$Target = Join-Path $env:LOCALAPPDATA 'MateMCP'", windows, StringComparison.Ordinal);
        Assert.Contains("$ModeFile = Join-Path (Join-Path $env:APPDATA 'MateMCP')", windows, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item $env:APPDATA", windows, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remove-Item -Recurse -Force $ModeFile", windows, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Windows Credential Manager", windows, StringComparison.Ordinal);

        // macOS updates replace only the binary payload. Durable state remains under
        // Library/Application Support/MateMCP, and elevated mode keeps the original
        // user's home/identity so the same metadata and Keychain entries are used.
        Assert.Contains("CONFIG=\"$HOME/Library/Application Support/MateMCP\"", mac, StringComparison.Ordinal);
        Assert.DoesNotContain("rm -rf \"$CONFIG\"", mac, StringComparison.Ordinal);
        Assert.Contains("MATEMCP_MAC_USER_HOME", macMode, StringComparison.Ordinal);
        Assert.Contains("MATEMCP_MAC_USER_NAME", macMode, StringComparison.Ordinal);

        Assert.Contains("Application Support", store, StringComparison.Ordinal);
        Assert.Contains("secrets.json", store, StringComparison.Ordinal);
        Assert.Contains("MATEMCP_MAC_USER_HOME", store, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MateMCP.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}