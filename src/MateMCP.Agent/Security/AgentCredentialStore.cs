using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MateMCP.Agent.Security;

public sealed record LocalAccessCredential(string Token);

public sealed class AgentCredentialStore
{
    private const string Service = "MateMCP.Agent";
    private const string LocalAccessAccount = "local-access-token";
    private const string AgentAccountPrefix = "agent:";

    public Task SaveAsync(string agentId, string credential, CancellationToken ct)
        => SaveSecretAsync(AgentAccountPrefix + agentId, credential, ct);

    public Task<string?> GetAsync(string agentId, CancellationToken ct)
        => GetSecretAsync(AgentAccountPrefix + agentId, ct);

    public Task DeleteAsync(string agentId, CancellationToken ct)
        => DeleteSecretAsync(AgentAccountPrefix + agentId, ct);

    public async Task<string> ResolveLocalAccessTokenAsync(string? configuredToken, string configurationPath, CancellationToken ct)
    {
        var environmentToken = Environment.GetEnvironmentVariable("MATEMCP_Mate__AccessToken");
        if (!string.IsNullOrWhiteSpace(environmentToken)) return environmentToken;

        var existing = await GetSecretAsync(LocalAccessAccount, ct);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            RemovePlaintextAccessToken(configurationPath);
            return existing;
        }

        var token = !string.IsNullOrWhiteSpace(configuredToken) && configuredToken != "change-me-before-exposing"
            ? configuredToken
            : "matemcp_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            await SaveSecretAsync(LocalAccessAccount, token, ct);
            RemovePlaintextAccessToken(configurationPath);
        }

        return token;
    }

    private static async Task SaveSecretAsync(string account, string credential, CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS())
        {
            using var process = NewSecurityProcess("add-generic-password", "-U", "-s", Service, "-a", account, "-w", credential);
            process.Start();
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(ct);
                throw new InvalidOperationException($"Could not save a MateMCP credential in macOS Keychain: {error.Trim()}");
            }
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            SaveWindowsCredential(account, credential);
            return;
        }

        throw new PlatformNotSupportedException("Secure credential storage is currently implemented for macOS Keychain and Windows Credential Manager.");
    }

    private static async Task<string?> GetSecretAsync(string account, CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS())
        {
            using var process = NewSecurityProcess("find-generic-password", "-s", Service, "-a", account, "-w");
            process.Start();
            var value = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0 ? value.Trim() : null;
        }

        if (OperatingSystem.IsWindows())
            return ReadWindowsCredential(account);

        return null;
    }

    private static async Task DeleteSecretAsync(string account, CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS())
        {
            using var process = NewSecurityProcess("delete-generic-password", "-s", Service, "-a", account);
            process.Start();
            await process.WaitForExitAsync(ct);
            if (process.ExitCode == 0) return;

            var error = (await process.StandardError.ReadToEndAsync(ct)).Trim();
            if (error.Contains("could not be found", StringComparison.OrdinalIgnoreCase)) return;
            throw new InvalidOperationException($"Could not delete a MateMCP credential from macOS Keychain: {error}");
        }

        if (OperatingSystem.IsWindows())
        {
            DeleteWindowsCredential(account);
            return;
        }

        throw new PlatformNotSupportedException("Secure credential storage is currently implemented for macOS Keychain and Windows Credential Manager.");
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
                throw new InvalidOperationException($"Could not save a MateMCP credential in Windows Credential Manager. Win32 error: {Marshal.GetLastWin32Error()}");
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
            return error == 1168 ? null : throw new InvalidOperationException($"Could not read a MateMCP credential from Windows Credential Manager. Win32 error: {error}");
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
        finally
        {
            CredFree(pointer);
        }
    }

    private static void DeleteWindowsCredential(string account)
    {
        var target = $"{Service}/{account}";
        if (CredDelete(target, 1, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error == 1168) return;
        throw new InvalidOperationException($"Could not delete a MateMCP credential from Windows Credential Manager. Win32 error: {error}");
    }

    private static void RemovePlaintextAccessToken(string path)
    {
        if (!File.Exists(path)) return;
        var root = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
        var mate = root?["Mate"]?.AsObject();
        if (root is null || mate is null || mate["AccessToken"] is null) return;

        mate.Remove("AccessToken");
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static Process NewSecurityProcess(params string[] arguments)
    {
        var start = new ProcessStartInfo("/usr/bin/security")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return new Process { StartInfo = start };
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

    [DllImport("Advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}
