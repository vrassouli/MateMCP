# Approval model

MateMCP distinguishes pre-authorized project operations from operations that require an explicit local decision.

## Local approval UI

The Agent exposes a loopback-only management UI at `http://127.0.0.1:45871/ui` (or the configured local port).

When an approval is created on macOS, MateMCP displays a desktop notification and opens the local management UI. Pending requests show the capability, target, summary, and expiry time, and can be **Approve**d or **Deny**ed directly from the UI. The underlying decision endpoints remain loopback-only and are not exposed through the relay.

Remote approval remains available through the control plane. Its status polling uses bounded backoff so a pending request does not generate a tight two-second HTTP loop for its full lifetime.

Current decisions:

- **Approve** — approve exactly one pending operation.
- **Deny** — reject the pending operation.

Planned policy extensions:

- **Allow for session** — approve matching operations for the current AI connection/session scope.
- **Always allow** — persist a narrowly scoped local policy rule.

Rules are owned by MateMCP, not MCP Roots.

Project-relative operations are pre-authorized according to project read/write/shell flags. Operations outside project roots must never silently fall back to unrestricted host access.

Persistent rules should be expressed in terms of capability + target scope, for example:

```text
filesystem.read /Users/me/Documents/example.txt
filesystem.write /Users/me/Exports/*
shell.exec project:PuyaStudio command-prefix:dotnet test
```

Broad rules such as unrestricted shell access or `/` filesystem access should require an explicit high-risk confirmation and remain visually distinguishable in the control UI.
