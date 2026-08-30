# MateMCP

MateMCP is a local, user-controlled MCP agent that gives AI clients controlled access to capabilities on a user's computer.

## v0.1 target

The first release targets macOS and direct MCP access over a user-configurable HTTPS port intended for explicit port forwarding.

Initial capabilities:

- Project-scoped filesystem access
- Shell command execution
- Policy and approval boundaries
- Audit logging
- Token-authenticated Streamable HTTP MCP endpoint

Future capabilities include desktop viewing/control, richer local approval UI, persistent terminal sessions, and a relay/tunnel transport.

> MateMCP treats configured project roots and its own policy engine as security boundaries. MCP Roots are not used as an authorization boundary.

## macOS install from a CI artifact

Download the artifact matching your Mac (`MateMCP-osx-arm64` for Apple Silicon or `MateMCP-osx-x64` for Intel), extract the archive, then run:

```bash
chmod +x install-macos.sh
./install-macos.sh
$HOME/.local/bin/matemcp
```

The first run creates a private configuration file at:

```text
~/Library/Application Support/MateMCP/appsettings.json
```

A random bearer token is generated automatically and the config file is restricted to the current user on macOS/Linux.

The safe first-run defaults bind only to `127.0.0.1:45871` using HTTP. Before exposing MateMCP through router/firewall port forwarding, configure a publicly reachable bind address and HTTPS certificate in the user configuration. MateMCP refuses public HTTPS mode without an explicit certificate.

Health/status endpoints:

```text
GET /health
GET /status
```

The MCP endpoint is `/mcp` and requires `Authorization: Bearer <token>`.
