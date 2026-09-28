# External login providers

MateMCP can keep the existing email/password flow while enabling selected external identity providers on the account portal.

Supported providers:

- Google
- Microsoft
- GitHub
- Apple

Providers are opt-in. A provider is not rendered on the Login or Register page unless it is enabled and has both a client ID and client secret configured.

## Production callback URLs

For the production control-plane host, register these callback URLs with the corresponding provider:

| Provider | Callback URL |
| --- | --- |
| Google | `https://api.matemcp.com/auth/callback/google` |
| Microsoft | `https://api.matemcp.com/auth/callback/microsoft` |
| GitHub | `https://api.matemcp.com/auth/callback/github` |
| Apple | `https://api.matemcp.com/auth/callback/apple` |

A deployment may override an individual `CallbackPath`, but it must remain an absolute local path beginning with a single `/`.

The reverse proxy must forward the original HTTPS scheme and host so the authentication middleware produces public HTTPS callback URLs.

## Configuration

Use deployment secrets or environment variables. Do not commit provider credentials.

Google example:

```text
MateMCP__ExternalAuth__Providers__Google__Enabled=true
MateMCP__ExternalAuth__Providers__Google__ClientId=<client-id>
MateMCP__ExternalAuth__Providers__Google__ClientSecret=<client-secret>
```

Microsoft:

```text
MateMCP__ExternalAuth__Providers__Microsoft__Enabled=true
MateMCP__ExternalAuth__Providers__Microsoft__ClientId=<client-id>
MateMCP__ExternalAuth__Providers__Microsoft__ClientSecret=<client-secret>
```

GitHub:

```text
MateMCP__ExternalAuth__Providers__GitHub__Enabled=true
MateMCP__ExternalAuth__Providers__GitHub__ClientId=<client-id>
MateMCP__ExternalAuth__Providers__GitHub__ClientSecret=<client-secret>
```

Apple:

```text
MateMCP__ExternalAuth__Providers__Apple__Enabled=true
MateMCP__ExternalAuth__Providers__Apple__ClientId=<services-id>
MateMCP__ExternalAuth__Providers__Apple__ClientSecret=<signed-client-secret>
```

Apple's client secret is a signed JWT with an expiration date. Generate and rotate it outside the repository.

An enabled provider with a missing client ID or client secret causes startup to fail with a configuration error instead of exposing a broken login button.

## Sign-in and registration behavior

External authentication uses authorization-code flows. OIDC providers use PKCE where supported by the ASP.NET Core handler.

MateMCP stores the provider's stable account identifier rather than treating email as the identity key.

For a new external identity:

1. MateMCP requires a usable verified/provider-trusted email.
2. If no local account has that email, MateMCP creates the account and links the external identity.
3. If a local MateMCP account already has that email, MateMCP does **not** silently link it. The user must sign in with the existing password and connect the provider from **Account > Sign-in methods**.

This explicit-link rule prevents possession of a newly asserted external email from silently taking over an existing local account.

Provider email handling:

- Google and Apple require the provider's verified-email claim.
- GitHub uses the verified email list returned by the GitHub API and prefers the primary verified address.
- Microsoft uses the email/preferred-username asserted by Microsoft. Matching an existing MateMCP email still does not auto-link accounts.

Disabled MateMCP accounts remain disabled even when a previously linked provider successfully authenticates the external identity.

## Account linking

An authenticated user can open `/account` and connect any enabled provider that is not already connected.

A single provider identity can belong to only one MateMCP account, and a MateMCP account can have at most one identity for a given provider. Linking actions are recorded in the audit log.

The current implementation intentionally does not provide unlinking. This avoids creating an account-recovery dead end until recovery/unlink policy is defined explicitly.

## Database compatibility

MateMCP currently uses an `EnsureCreated` schema flow. External logins therefore include an idempotent startup schema upgrade for both supported database providers:

- SQLite
- SQL Server

The upgrade creates the external-login table and uniqueness constraints for existing deployments without requiring a manual EF migration.

## Verification checklist

Before enabling a provider in production:

1. Register the exact production callback URL at the provider.
2. Store its client credentials in deployment secrets/environment.
3. Restart the API and confirm the provider appears on both Login and Register.
4. Test new-account registration with that provider.
5. Test sign-in on the linked account.
6. Test cancellation/denied consent and confirm the user returns to a friendly error state.
7. Test an email that already belongs to a local account and confirm it is not auto-linked.
8. Sign in locally, connect the provider from Account, sign out, then sign in with the provider.
9. Confirm a disabled linked account cannot sign in.
10. Check the audit trail and browser/server logs for unexpected errors without exposing provider tokens or secrets.
