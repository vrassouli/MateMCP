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
    CredentialKind Kind = CredentialKind.Password);

public interface ICredentialStore
{
    Task<IReadOnlyList<UserSecretInfo>> ListAsync(CancellationToken ct);
    Task SaveAsync(string name, string value, string? description, CredentialKind kind, CancellationToken ct);
    Task<string?> ResolveAsync(string name, CancellationToken ct);
    Task<bool> DeleteAsync(string name, CancellationToken ct);
}
