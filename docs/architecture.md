# Architecture

MateMCP v0.1 is a macOS-first local agent. The MCP endpoint uses Streamable HTTP and is designed to be exposed deliberately through router/firewall port forwarding.

## Boundaries

- Project roots are configured by the user and enforced by MateMCP's own path resolver.
- MCP Roots are not treated as an authorization mechanism.
- `/mcp` requires a bearer token.
- Public deployments must use HTTPS; insecure HTTP exists only as an explicit development override.
- File operations are project-relative and canonicalized before access.
- Shell execution uses a configured project as its working directory.
- Every implemented filesystem and shell action writes an audit entry.

## Future transport

Capabilities do not depend on direct networking. A later Relay/Tunnel transport can replace direct exposure while keeping the same policy and tool layers.

## Approval model

The full approval workflow is tracked separately. Until it is implemented, v0.1 tools intentionally operate only inside explicitly configured project roots; out-of-root access is denied rather than implicitly approved.
