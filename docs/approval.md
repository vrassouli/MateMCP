# Approval model

MateMCP distinguishes pre-authorized project operations from operations that require an explicit local decision.

Planned decisions:

- **Allow once** — approve exactly one pending operation.
- **Allow for session** — approve matching operations for the current AI connection/session scope.
- **Always allow** — persist a narrowly scoped local policy rule.
- **Deny** — reject the pending operation.

Rules are owned by MateMCP, not MCP Roots.

For v0.1, project-relative operations are pre-authorized according to project read/write/shell flags. Operations outside project roots are denied until the local approval surface is available; they must never silently fall back to unrestricted host access.

Persistent rules should be expressed in terms of capability + target scope, for example:

```text
filesystem.read /Users/me/Documents/example.txt
filesystem.write /Users/me/Exports/*
shell.exec project:PuyaStudio command-prefix:dotnet test
```

Broad rules such as unrestricted shell access or `/` filesystem access should require an explicit high-risk confirmation and remain visually distinguishable in the control UI.
