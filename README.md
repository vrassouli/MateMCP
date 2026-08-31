# MateMCP

MateMCP gives an AI client controlled, project-scoped access to a user's computer through a local Agent, Relay, and OAuth Control Plane.

## User flow

1. Install and start the Agent.
2. The Agent opens MateMCP in the browser and shows a short device code.
3. Sign in with a personal MateMCP account and approve adding the device.
4. The Agent stores its private credential in macOS Keychain and appears in the account dashboard.
5. Copy that Agent's unique MCP URL, for example `https://relay.matemcp.com/mcp/agt_...`, into ChatGPT.
6. Complete OAuth using the same personal account.
7. Sensitive operations can be allowed or denied from the local Agent endpoint or the web dashboard on a phone.

There are no Agent tokens to copy and no shared admin account in the normal user flow. Each user can own multiple independently revocable Agents. OAuth tokens are accepted only when their user, Agent, resource, and scopes all match.

## macOS Agent

```bash
curl -fsSL https://raw.githubusercontent.com/vrassouli/MateMCP/feat/multi-user-control-plane/scripts/bootstrap-macos.sh | bash
matemcp
```

The private configuration is stored at `~/Library/Application Support/MateMCP/appsettings.json`; the enrolled Agent credential is stored in macOS Keychain. Local approvals remain available at `http://127.0.0.1:45871/approvals` with `POST /approvals/{id}/allow` and `/deny`.

## API / Control Plane

```bash
curl -fsSL https://raw.githubusercontent.com/vrassouli/MateMCP/feat/multi-user-control-plane/deploy/api/install.sh | sudo bash
```

The installer asks for public API and Relay URLs and a database provider:

- `sqlite`: persistent local Docker volume, suitable for a small single-server deployment.
- `sqlserver`: asks for host, port, database, username, password, encryption, and certificate trust settings. The connection string is passed base64-encoded so special characters in passwords are preserved. Startup validates connectivity while creating the schema.

It also creates a private internal API key used between Relay and Control Plane. Copy that value from `/opt/matemcp-api/.env` only into the Relay installer prompt; end users never see it.

## Relay

```bash
curl -fsSL https://raw.githubusercontent.com/vrassouli/MateMCP/feat/multi-user-control-plane/deploy/relay/install.sh | sudo bash
```

The Relay installer asks for its public URL, the public/internal Control Plane URLs, and the Control Plane internal API key. It no longer generates shared Agent or Client tokens.

Both services must sit behind HTTPS reverse proxies. Their container ports (`8080` and `8081`) should not be exposed directly to the Internet.

## Security boundaries

- Users can authorize only Agents they own.
- Every Agent has a random public ID and a separate high-entropy Keychain credential.
- The unique MCP URL is an identifier, not a secret.
- OAuth resource and `agent_id` claims must match the requested URL.
- `mcp:read`, `mcp:write`, and `mcp:shell` are enforced per JSON-RPC tool call by Relay and constrained by the Agent's allowed scopes.
- Filesystem paths remain confined to configured project roots.
- Shell execution retains explicit approval and audit logging.
- Remote approvals are owner-bound, operation-hashed, one-use decisions with expiration; local approval remains available if Control Plane is unreachable.
