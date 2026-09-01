# MateMCP

MateMCP gives an AI client controlled, project-scoped access to a user's computer through a local Agent, Relay, and OAuth Control Plane.

## User flow

1. Install and start the Agent.
2. The Agent opens MateMCP in the browser and shows a short device code.
3. Sign in with a personal MateMCP account and approve adding the device.
4. The Agent stores its private credential in the operating system's secure credential store and appears in the account dashboard.
5. Copy that Agent's unique MCP URL, for example `https://relay.matemcp.com/mcp/agt_...`, into ChatGPT.
6. Complete OAuth using the same personal account.
7. Sensitive operations can be allowed or denied from the local Agent management UI or the web dashboard on a phone.

There are no Agent tokens to copy and no shared admin account in the normal user flow. Each user can own multiple independently revocable Agents. OAuth tokens are accepted only when their user, Agent, resource, and scopes all match.

## macOS Agent

```bash
curl -fsSL https://raw.githubusercontent.com/vrassouli/MateMCP/main/scripts/bootstrap-macos.sh | bash
```

The bootstrap downloads the current stable package from the `agent-latest` GitHub Release, installs a per-user LaunchAgent, and starts it immediately. It also starts automatically on future sign-ins. The private configuration is stored at `~/Library/Application Support/MateMCP/appsettings.json`; enrolled Agent credentials and user-managed secrets are stored in macOS Keychain. Local approvals, secret management, credential audit, and project management are available at `http://127.0.0.1:45871/ui`.

## Windows Agent

Run this from PowerShell:

```powershell
irm https://raw.githubusercontent.com/vrassouli/MateMCP/main/scripts/bootstrap-windows.ps1 | iex
```

The bootstrap script detects Windows architecture automatically (`win-x64` or `win-arm64`), downloads the current stable Agent package from the `agent-latest` GitHub Release, extracts it to a temporary directory, and runs the packaged installer. No manual ZIP download or extraction is required.

The installer starts the Agent immediately and creates a per-user startup shortcut for future sign-ins. The private configuration is stored under `%APPDATA%\MateMCP`; enrolled Agent credentials are stored in Windows Credential Manager. The installer preserves configuration and credentials during normal upgrades so the enrolled device identity remains stable.

The supported Agent capability matrix and the checklist for keeping platforms aligned are documented in [`docs/agent-feature-parity.md`](docs/agent-feature-parity.md).

## API / Control Plane

For the usual single-server deployment, install the API and Relay together:

```bash
curl -fsSL https://raw.githubusercontent.com/vrassouli/MateMCP/main/deploy/install.sh | sudo bash
```

The combined installer asks for the public URLs once, configures the database, shares the private internal key automatically, and verifies both services. Use the component installers below only when API and Relay run on different servers or need to be managed independently.

```bash
curl -fsSL https://raw.githubusercontent.com/vrassouli/MateMCP/main/deploy/api/install.sh | sudo bash
```

The installer asks for public API and Relay URLs and a database provider:

- `sqlite`: persistent local Docker volume, suitable for a small single-server deployment.
- `sqlserver`: asks for host, port, database, username, password, encryption, and certificate trust settings. The connection string is passed base64-encoded so special characters in passwords are preserved. Startup validates connectivity while creating the schema.

It also creates a private internal API key used between Relay and Control Plane. Copy that value from `/opt/matemcp-api/.env` only into the Relay installer prompt; end users never see it.

## Relay

```bash
curl -fsSL https://raw.githubusercontent.com/vrassouli/MateMCP/main/deploy/relay/install.sh | sudo bash
```

The Relay installer asks for its public URL, the public/internal Control Plane URLs, and the Control Plane internal API key. It does not generate shared Agent or Client tokens.

Both services must sit behind HTTPS reverse proxies. Their container ports (`8080` and `8081`) should not be exposed directly to the Internet.

## Security boundaries

- Users can authorize only Agents they own.
- Every Agent has a random public ID and a separate high-entropy credential stored in macOS Keychain or Windows Credential Manager.
- The unique MCP URL is an identifier, not a secret.
- OAuth resource and `agent_id` claims must match the requested URL.
- `mcp:read`, `mcp:write`, and `mcp:shell` are enforced per JSON-RPC tool call by Relay and constrained by the Agent's allowed scopes.
- Filesystem paths remain confined to configured project roots.
- Shell execution retains explicit approval and audit logging.
- Remote approvals are owner-bound, operation-hashed, one-use decisions with expiration; local approval remains available if Control Plane is unreachable.

## Releases

`main` is the source of truth for stable development. Version tags such as `v0.1.0` publish self-contained Agent packages for macOS arm64/x64 and Windows x64/arm64.
