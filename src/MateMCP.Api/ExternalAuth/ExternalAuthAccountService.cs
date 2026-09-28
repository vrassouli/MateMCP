using MateMCP.Api.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace MateMCP.Api.ExternalAuth;

public sealed record ExternalAuthCompletionResult(
    UserAccount? User,
    string? Error,
    bool Created = false,
    bool Linked = false)
{
    public bool Succeeded => User is not null && Error is null;

    public static ExternalAuthCompletionResult Fail(string error) => new(null, error);
}

public sealed class ExternalAuthAccountService(
    ControlPlaneDbContext db,
    IPasswordHasher<UserAccount> passwordHasher)
{
    public async Task<ExternalAuthCompletionResult> CompleteAsync(
        string provider,
        string providerKey,
        string? email,
        bool emailVerified,
        Guid? explicitLinkUserId,
        CancellationToken cancellationToken = default)
    {
        provider = provider.Trim().ToLowerInvariant();
        providerKey = providerKey.Trim();

        if (provider.Length == 0 || providerKey.Length == 0)
            return ExternalAuthCompletionResult.Fail("The external provider did not return a stable account identifier.");

        var existingLogin = await db.ExternalLogins
            .Include(x => x.User)
            .SingleOrDefaultAsync(
                x => x.Provider == provider && x.ProviderKey == providerKey,
                cancellationToken);

        if (existingLogin is not null)
        {
            if (existingLogin.User is null || existingLogin.User.IsDisabled)
                return ExternalAuthCompletionResult.Fail("This MateMCP account is disabled.");

            if (explicitLinkUserId is not null && existingLogin.UserAccountId != explicitLinkUserId.Value)
                return ExternalAuthCompletionResult.Fail("This external identity is already connected to a different MateMCP account.");

            return new ExternalAuthCompletionResult(existingLogin.User, null);
        }

        var normalizedEmail = NormalizeVerifiedEmail(email, emailVerified);
        if (normalizedEmail is null)
            return ExternalAuthCompletionResult.Fail("The external provider did not return a verified email address. Use another provider or sign in with your MateMCP password.");

        if (explicitLinkUserId is not null)
        {
            var user = await db.Users.SingleOrDefaultAsync(
                x => x.Id == explicitLinkUserId.Value,
                cancellationToken);
            if (user is null || user.IsDisabled)
                return ExternalAuthCompletionResult.Fail("Your MateMCP account is no longer available.");

            var alreadyConnected = await db.ExternalLogins.AnyAsync(
                x => x.UserAccountId == user.Id && x.Provider == provider,
                cancellationToken);
            if (alreadyConnected)
                return ExternalAuthCompletionResult.Fail("This provider is already connected to your MateMCP account.");

            AddLogin(user, provider, providerKey, email!.Trim());
            db.AuditEvents.Add(new AuditEvent
            {
                UserId = user.Id,
                EventType = "external_login_linked",
                Detail = $"provider={provider}"
            });
            await db.SaveChangesAsync(cancellationToken);
            return new ExternalAuthCompletionResult(user, null, Linked: true);
        }

        var existingAccount = await db.Users.SingleOrDefaultAsync(
            x => x.NormalizedEmail == normalizedEmail,
            cancellationToken);
        if (existingAccount is not null)
        {
            return ExternalAuthCompletionResult.Fail(
                "A MateMCP account already exists for this email. Sign in with your password first, then connect this provider from Account.");
        }

        var account = new UserAccount
        {
            Email = email!.Trim(),
            NormalizedEmail = normalizedEmail,
            PasswordHash = "pending"
        };
        var unreachablePassword = Base64UrlEncoder.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        account.PasswordHash = passwordHasher.HashPassword(account, unreachablePassword);

        db.Users.Add(account);
        AddLogin(account, provider, providerKey, account.Email);
        db.AuditEvents.Add(new AuditEvent
        {
            UserId = account.Id,
            EventType = "external_account_created",
            Detail = $"provider={provider}"
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return ExternalAuthCompletionResult.Fail(
                "MateMCP could not finish creating the account because it changed concurrently. Please try again.");
        }

        return new ExternalAuthCompletionResult(account, null, Created: true, Linked: true);
    }

    private void AddLogin(UserAccount user, string provider, string providerKey, string email)
    {
        db.ExternalLogins.Add(new ExternalLogin
        {
            UserAccountId = user.Id,
            User = user,
            Provider = provider,
            ProviderKey = providerKey,
            Email = email
        });
    }

    private static string? NormalizeVerifiedEmail(string? email, bool verified)
    {
        if (!verified || string.IsNullOrWhiteSpace(email))
            return null;

        var value = email.Trim();
        if (value.Length > 320 || !value.Contains('@', StringComparison.Ordinal))
            return null;

        return value.ToUpperInvariant();
    }
}
