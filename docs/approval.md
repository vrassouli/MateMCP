# Approval model

MateMCP distinguishes pre-authorized project operations from operations that require an explicit local decision.

## Local approval UI

The Agent exposes a loopback-only management UI at `http://127.0.0.1:45871/ui` (or the configured local port).

When an approval is created on macOS, MateMCP displays a desktop notification and opens the local management UI. Pending requests show the capability, target, summary, and expiry time, and can be **Approve**d or **Deny**ed directly from the UI. The underlying decision endpoints remain loopback-only and are not exposed through the relay.

Remote approval remains available through the control plane. Its status polling uses bounded backoff so a pending request does not generate a tight two-second HTTP loop for its full lifetime.

Current decisions:

- **Allow once** — approve exactly one pending operation.
- **Allow for session** — cache the exact capability + target rule in memory until the Agent stops.
- **Always allow** — persist the exact capability + target rule in the local policy file.
- **Deny** — reject the pending operation.

Credential approvals use `secret.use` as the capability and `<credential>@cmd:<fingerprint>` as the target. This binds session and persistent approval rules to both the named credential and the exact interactive command; a rule for another credential or command does not match. Persistent rules can be inspected and removed from the loopback-only management UI.

Rules are owned by MateMCP, not MCP Roots.

Project-relative operations are pre-authorized according to project read/write/shell flags. Operations outside project roots must never silently fall back to unrestricted host access.

Persistent rules are expressed in terms of capability + target scope, for example:

```text
filesystem.read /Users/me/Documents/example.txt
filesystem.write /Users/me/Exports/*
shell.exec project:PuyaStudio command-prefix:dotnet test
```

Broad rules such as unrestricted shell access or `/` filesystem access should require an explicit high-risk confirmation and remain visually distinguishable in the control UI.
