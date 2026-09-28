---
name: account-security
description: Security rules for MateMCP user/admin accounts, disabled users, administrative actions, and portal session enforcement.
---

# MateMCP account and administration security

Use this skill when changing authentication, account status, administration, Agent ownership, or security-sensitive portal actions.

## Authorization

- `UserAccount.IsAdmin` is the source of the portal administrator role.
- Administration endpoints must enforce the `admin` authorization policy server-side. Hiding navigation or buttons is never sufficient.
- A regular authenticated user requesting an admin endpoint should receive an explicit forbidden response.
- Never render password hashes, Agent credential hashes, raw Agent credentials, refresh tokens, private keys, or other secrets in administrative HTML.

## Disabled accounts

- `UserAccount.IsDisabled` is an immediate account-level access switch.
- A disabled account must be rejected on the next authenticated web request even when an older auth cookie exists.
- Agents owned by a disabled account must fail Agent authentication, authorization, approval creation/status access, and owner-scoped device-management authentication.
- Disabling an account does **not** revoke its AgentDevice records. Re-enabling the account restores account-level access to devices that were not separately revoked.
- Device revoke remains a distinct permanent credential action.

## Administrator safety

- An administrator cannot disable their own currently signed-in account.
- Never permit disabling the last enabled administrator account.
- Bootstrap-admin configuration must be idempotent for existing databases: create the configured admin when absent, or promote the matching existing account to admin without resetting that account's password on every startup.
- Security-sensitive admin actions must create audit events that identify the acting administrator and affected account/device.

## Portal mutations

- State-changing browser form POSTs use anti-forgery validation.
- Confirmation screens are required before destructive/security-sensitive actions such as disabling an account or revoking another user's device.
- Invalid anti-forgery tokens must fail closed without applying a mutation.
- Keep success/error feedback explicit after administrative actions.

## Verification

For account/admin changes, test at minimum:

- regular-user denial of admin routes;
- admin list/search/detail access;
- disabled user's existing session rejection;
- disabled owner's Agent access rejection;
- re-enable recovery;
- self/last-admin safety;
- device revoke + audit;
- anti-forgery rejection;
- no secret/hash rendering;
- responsive browser QA at mobile, intermediate, and wide desktop widths.
