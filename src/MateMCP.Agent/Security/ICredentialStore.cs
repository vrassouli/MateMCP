namespace MateMCP.Agent.Security;

public enum CredentialKind
{
    Password,
    Token,
    SshPassphrase,
    Generic
}

public sealed record UserSecretInfo(
    string Name,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    CredentialKind Kind = CredentialKind.Password,
    IReadOnlyList<string>? AllowedTools = null)
{
    public const string ShellSessionSendSecretTool = "shell_session_send_secret";
    public const string SshSessionAuthenticateTool = "ssh_session_authenticate";

    public IReadOnlyList<string> EffectiveAllowedTools =>
        AllowedTools is null ? [ShellSessionSendSecretTool] : AllowedTools;

    public bool IsAllowedForTool(string tool) =>
        EffectiveAllowedTools.Contains(tool, StringComparer.Ordinal);
}

public interface ICredentialStore
{
    Task<IReadOnlyList<UserSecretInfo>> ListAsync(CancellationToken ct);
    Task SaveAsync(string name, string value, string? description, CredentialKind kind,
        IReadOnlyCollection<string>? allowedTools, CancellationToken ct);
    Task<string?> ResolveAsync(string name, CancellationToken ct);
    Task<bool> DeleteAsync(string name, CancellationToken ct);
}
