# MateMCP

MateMCP is a local, user-controlled MCP agent that gives AI clients access to selected capabilities on a user's computer.

## v0.1 target

The first release targets macOS and direct MCP access over a user-configurable HTTPS port intended for explicit port forwarding.

Initial capabilities:

- Project-scoped filesystem access
- Shell command execution
- Policy and approval boundaries
- Audit logging
- Token-authenticated Streamable HTTP MCP endpoint

Future capabilities include desktop viewing/control, richer local approval UI, and a relay/tunnel transport.

> MateMCP treats configured project roots and its own policy engine as security boundaries. MCP Roots are not used as an authorization boundary.
