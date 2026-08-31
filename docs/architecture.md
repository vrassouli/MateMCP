# Architecture

MateMCP is a macOS-first local Agent connected through a Relay and OAuth Control Plane. Direct port forwarding is no longer required for the normal product flow.

## Boundaries

- Project roots are configured by the user and enforced by MateMCP's own path resolver.
- MCP Roots are not treated as an authorization mechanism.
- Every remote `/mcp/{agentId}` URL requires an OAuth token issued to the Agent owner for that exact Agent resource.
- Public deployments must use HTTPS; insecure HTTP exists only as an explicit development override.
- File operations are project-relative and canonicalized before access.
- Shell execution uses a configured project as its working directory.
- Every implemented filesystem and shell action writes an audit entry.

## Identity and enrollment

The Agent uses a device-authorization enrollment flow. The user signs in in a browser, approves the device, and the Control Plane issues a per-Agent credential. The credential is stored in macOS Keychain. Relay validates it with the Control Plane at WebSocket connection time.

## Approval model

Shell approval is shown locally and published to the owner's dashboard. Either channel can decide the request. Remote requests contain an operation hash and expiration; decisions are one-use and audit logged. Out-of-root access is always denied rather than implicitly approved.
