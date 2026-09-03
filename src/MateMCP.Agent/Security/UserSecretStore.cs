using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MateMCP.Agent.Configuration;

namespace MateMCP.Agent.Security;

public sealed class UserSecretStore : ICredentialStore
{
    private const string Service = "MateMCP.Agent";
    private const string AccountPrefix = "user-secret:";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _indexPath;

    public UserSecretStore() : this(GetDefaultIndexPath())
    {
    }

    public UserSecretStore(string indexPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(indexPath))
            ?? throw new ArgumentException("Secret index path must include a directory.", nameof(indexPath));
        Directory.CreateDirectory(directory);
        ConfigurationBootstrap.TryRestoreDelegatedMacOwnership(directory);
        _indexPath = Path.Combine(directory, Path.GetFileName(indexPath));
    }

    private static string GetDefaultIndexPath()
    {
        var applicationDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MateMCP",
            "secrets.json");

        if (!OperatingSystem.IsMacOS()) return applicationDataPath;

        // Keep secret metadata in the original user's stable data directory even
        // when the Agent itself is running as a root LaunchDaemon.
        var userHome = Environment.GetEnvironmentVariable("MATEMCP_MAC_USER_HOME");
        if (string.IsNullOrWhiteSpace(userHome))
            userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stablePath = Path.Combine(userHome, "Library", "Application Support", "MateMCP", "secrets.json");

        if (string.Equals(Path.GetFullPath(stablePath), Path.GetFullPath(applicationDataPath), StringComparison.Ordinal))
            return stablePath;

        try
        {
            if (!File.Exists(stablePath) && File.Exists(applicationDataPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stablePath)!);
                ConfigurationBootstrap.TryRestoreDelegatedMacOwnership(Path.GetDirectoryName(stablePath)!);
                File.Copy(applicationDataPath, stablePath, overwrite: false);
                ConfigurationBootstrap.TryRestrictPermissions(stablePath);
            }
        }
        catch (IOException)
        {
            if (!File.Exists(stablePath)) throw;
        }

        return stablePath;
    }

    public async Task<IReadOnlyList<UserSecretInfo>> ListAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return (await ReadIndexAsync(ct)).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray(); }
        finally { _gate.Release(); }
    }

    public Task SaveAsync(string name, string value, string? description, CancellationToken ct)
        => SaveAsync(name, value, description, CredentialKind.Password, null, ct);

    public async Task SaveAsync(string name, string value, string? description, CredentialKind kind,
        IReadOnlyCollection<string>? allowedTools, CancellationToken ct)
    {
        name = NormalizeName(name);
        var normalizedTools = NormalizeAllowedTools(allowedTools);
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("Secret value cannot be empty.", nameof(value));
        if (value.Length > 16_384) throw new ArgumentException("Secret value is too large.", nameof(value));
        EnsureSupported();

        await SavePlatformSecretAsync(AccountPrefix + name, value, ct);
        await _gate.WaitAsync(ct);
        try
        {
            var items = await ReadIndexAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var existing = items.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                var old = items[existing];
                items[existing] = old with
                {
                    Name = name,
                    Description = CleanDescription(description),
                    UpdatedAt = now,
                    Kind = kind,
                    AllowedTools = normalizedTools
                };
            }
            else
            {
                items.Add(new UserSecretInfo(name, CleanDescription(description), now, now, kind, normalizedTools));
            }
            await WriteIndexAsync(items, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> ResolveAsync(string name, CancellationToken ct)
    {
        name = NormalizeName(name);
        EnsureSupported();
        return await ReadPlatformSecretAsync(AccountPrefix + name, ct);
    }

    public async Task<bool> DeleteAsync(string name, CancellationToken ct)
    {
        name = NormalizeName(name);
        EnsureSupported();
        await DeletePlatformSecretAsync(AccountPrefix + name, ct);

        await _gate.WaitAsync(ct);
        try
        {
            var items = await ReadIndexAsync(ct);
            var removed = items.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) await WriteIndexAsync(items, ct);
            return removed;
        }
        finally { _gate.Release(); }
    }

    private async Task<List<UserSecretInfo>> ReadIndexAsync(CancellationToken ct)
    {
        if (!File.Exists(_indexPath)) return [];
        await using var stream = File.OpenRead(_indexPath);
        return await JsonSerializer.DeserializeAsync<List<UserSecretInfo>>(stream, cancellationToken: ct) ?? [];
    }

    private async Task WriteIndexAsync(List<UserSecretInfo> items, CancellationToken ct)
    {
        await using (var stream = new FileStream(_indexPath, FileMode.Create, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(stream, items, new JsonSerializerOptions { WriteIndented = true }, ct);
        ConfigurationBootstrap.TryRestrictPermissions(_indexPath);
    }

    private static string NormalizeName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 100) throw new ArgumentException("Secret name must be between 1 and 100 characters.", nameof(name));
        if (name.Any(c => !char.IsLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new ArgumentException("Secret name may contain only letters, digits, '.', '-' and '_'.", nameof(name));
        return name;
    }

    private static string? CleanDescription(string? description)
    {
        description = description?.Trim();
        if (string.IsNullOrEmpty(description)) return null;
        return description.Length <= 300 ? description : description[..300];
    }

    private static IReadOnlyList<string> NormalizeAllowedTools(IReadOnlyCollection<string>? allowedTools)
    {
        allowedTools ??= [UserSecretInfo.ShellSessionSendSecretTool];
        var normalized = allowedTools
            .Select(x => x?.Trim() ?? string.Empty)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Any(x => x.Length > 100 || x.Any(c => !char.IsLetterOrDigit(c) && c is not '_' and not '-' and not '.')))
            throw new ArgumentException("Allowed tool names may contain only letters, digits, '.', '-' and '_' and must be at most 100 characters.", nameof(allowedTools));
        return normalized;
    }

    private static void EnsureSupported()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Named secret storage currently requires macOS Keychain or Windows Credential Manager.");
    }

    private static async Task SavePlatformSecretAsync(string account, string credential, CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS())
        {
            using var process = NewSecurityProcess("add-generic-password", "-U", "-s", Service, "-a", account, "-w", credential);
            process.Start();
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(ct);
                throw new InvalidOperationException($"Could not save secret in macOS Keychain: {error.Trim()}");
            }
            return;
        }

        SaveWindowsCredential(account, credential);
    }

    private static async Task<string?> ReadPlatformSecretAsync(string account, CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS())
        {
            using var process = NewSecurityProcess("find-generic-password", "-s", Service, "-a", account, "-w");
            process.Start();
            var value = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0 ? value.TrimEnd('\r', '\n') : null;
        }

        return ReadWindowsCredential(account);
    }

    private static async Task DeletePlatformSecretAsync(string account, CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS())
        {
            using var process = NewSecurityProcess("delete-generic-password", "-s", Service, "-a", account);
            process.Start();
            await process.WaitForExitAsync(ct);
            return;
        }

        DeleteWindowsCredential(account);
    }

    private static Process NewSecurityProcess(params string[] arguments)
    {
        var delegatedUser = Environment.GetEnvironmentVariable("MATEMCP_MAC_USER_NAME");
        var delegatedUid = Environment.GetEnvironmentVariable("MATEMCP_MAC_USER_UID");
        ProcessStartInfo start;

        if (ProcessPrivilege.IsElevated() &&
            !string.IsNullOrWhiteSpace(delegatedUser) &&
            uint.TryParse(delegatedUid, out _))
        {
            // Root has a different login Keychain. Enter the enrolled user's GUI
            // bootstrap namespace and drop privileges only for the security(1)
            // operation so existing secrets remain in that user's Keychain.
            start = new ProcessStartInfo("/bin/launchctl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("asuser");
            start.ArgumentList.Add(delegatedUid!);
            start.ArgumentList.Add("/usr/bin/sudo");
            start.ArgumentList.Add("-u");
            start.ArgumentList.Add(delegatedUser);
            start.ArgumentList.Add("/usr/bin/security");
        }
        else
        {
            start = new ProcessStartInfo("/usr/bin/security")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return new Process { StartInfo = start };
    }

    private static void SaveWindowsCredential(string account, string credential)
    {
        var target = $"{Service}/{account}";
        var bytes = Encoding.UTF8.GetBytes(credential);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var native = new NativeCredential
            {
                Type = 1,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = 2,
                UserName = Environment.UserName
            };
            if (!CredWrite(ref native, 0))
                throw new InvalidOperationException($"Could not save secret in Windows Credential Manager. Win32 error: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeHGlobal(blob);
        }
    }

    private static string? ReadWindowsCredential(string account)
    {
        var target = $"{Service}/{account}";
        if (!CredRead(target, 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == 1168 ? null : throw new InvalidOperationException($"Could not read secret from Windows Credential Manager. Win32 error: {error}");
        }

        try
        {
            var native = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (native.CredentialBlob == IntPtr.Zero || native.CredentialBlobSize == 0) return string.Empty;
            var bytes = new byte[checked((int)native.CredentialBlobSize)];
            Marshal.Copy(native.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    private static void DeleteWindowsCredential(string account)
    {
        if (!CredDelete($"{Service}/{account}", 1, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168) throw new InvalidOperationException($"Could not delete secret from Windows Credential Manager. Win32 error: {error}");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite([In] ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
