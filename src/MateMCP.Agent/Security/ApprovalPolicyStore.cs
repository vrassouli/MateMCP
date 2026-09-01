using System.Collections.Concurrent;
using System.Text.Json;

namespace MateMCP.Agent.Security;

public sealed record ApprovalPolicy(string Capability, string Target, DateTimeOffset CreatedAt);

public sealed class ApprovalPolicyStore
{
    private readonly ConcurrentDictionary<string, ApprovalPolicy> _session = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;

    public ApprovalPolicyStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MateMCP", "approval-policies.json")) { }

    public ApprovalPolicyStore(string path)
    {
        _path = path;
    }

    public bool IsSessionAllowed(string capability, string target) => _session.ContainsKey(Key(capability, target));

    public void AllowForSession(string capability, string target) =>
        _session[Key(capability, target)] = new ApprovalPolicy(capability, target, DateTimeOffset.UtcNow);

    public async Task<bool> IsAlwaysAllowedAsync(string capability, string target, CancellationToken cancellationToken = default)
    {
        var policies = await ReadAsync(cancellationToken);
        return policies.Any(x => Matches(x, capability, target));
    }

    public async Task AllowAlwaysAsync(string capability, string target, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var policies = await ReadCoreAsync(cancellationToken);
            if (!policies.Any(x => Matches(x, capability, target)))
            {
                policies.Add(new ApprovalPolicy(capability, target, DateTimeOffset.UtcNow));
                await WriteCoreAsync(policies, cancellationToken);
            }
        }
        finally { _gate.Release(); }
    }

    public Task<IReadOnlyList<ApprovalPolicy>> GetAlwaysAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(cancellationToken);

    public async Task<bool> RemoveAlwaysAsync(string capability, string target, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var policies = await ReadCoreAsync(cancellationToken);
            var removed = policies.RemoveAll(x => Matches(x, capability, target)) > 0;
            if (removed) await WriteCoreAsync(policies, cancellationToken);
            return removed;
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<ApprovalPolicy>> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadCoreAsync(cancellationToken)).ToArray(); }
        finally { _gate.Release(); }
    }

    private async Task<List<ApprovalPolicy>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<ApprovalPolicy>>(stream, cancellationToken: cancellationToken) ?? [];
    }

    private async Task WriteCoreAsync(List<ApprovalPolicy> policies, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, policies.OrderBy(x => x.Capability).ThenBy(x => x.Target).ToArray(), cancellationToken: cancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string Key(string capability, string target) => capability + "\n" + target;
    private static bool Matches(ApprovalPolicy policy, string capability, string target) =>
        string.Equals(policy.Capability, capability, StringComparison.Ordinal) && string.Equals(policy.Target, target, StringComparison.Ordinal);
}
