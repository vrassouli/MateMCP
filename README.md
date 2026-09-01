# MateMCP

MateMCP gives an AI client controlled, project-scoped access to a user's computer through a local Agent, Relay, and OAuth Control Plane.

## User flow

1. Install MateMCP Desktop. The installer installs or upgrades both the local Agent and the native Companion UI.
2. The Agent opens MateMCP in the browser and shows a short device code when enrollment is required.
3. Sign in with a personal MateMCP account and approve adding the device.
4. The Agent stores its private credential in the operating system's secure credential store and appears in the account dashboard.
5. Copy that Agent's unique MCP URL, for example `https://relay.matemcp.com/mcp/agt_...`, into ChatGPT.
6. Complete OAuth using the same personal account.
7. Sensitive operations can be allowed or denied from the native Agent Companion or the web dashboard on a phone.

There are no Agent tokens to copy and no shared admin account in the normal user flow. Each user can own multiple independently revocable Agents. OAuth tokens are accepted only when their user, Agent, resource, and scopes all match.

## Install / upgrade on macOS

For Apple Silicon Macs, this single command downloads the latest stable MateMCP Desktop package, extracts it, upgrades any existing Agent installation, installs the native Companion, starts the Agent, opens Companion for the interactive install, and cleans up the temporary files:

```bash
curl -fsSL https://raw.githubusercontent.com/vrassouli/MateMCP/main/scripts/bootstrap-macos.sh | bash
```

**No manual uninstall is required when upgrading.** Existing configuration, enrollment identity, and Keychain-backed credentials/secrets are preserved.

Prefer a manual download instead? Use the stable Desktop asset:

- [Download MateMCP Desktop for macOS Apple Silicon](https://github.com/vrassouli/MateMCP/releases/download/agent-latest/MateMCP-Desktop-macos-arm64.tar.gz)
- [View the latest stable release](https://github.com/vrassouli/MateMCP/releases/tag/agent-latest)

After extracting the archive, run `./install-macos.sh`. In a Desktop package that entry point installs **both Agent + Companion**. The lower-level Agent installer automatically delegates to the Desktop installer when the Companion payload is present.

The Agent runs in the background as a per-user LaunchAgent. The native Companion is installed in `~/Applications/MateMCP Agent Companion.app` but does **not** automatically open after future sign-ins. Open it when you want to manage the Agent, approvals, secrets, interactive sessions, or updates. Private configuration is stored at `~/Library/Application Support/MateMCP/appsettings.json`; enrolled Agent credentials and user-managed secrets are stored in macOS Keychain.

From Companion you can monitor whether the Agent is running and Start, Stop, or Restart it without using Terminal. On Apple Silicon, Companion also checks the moving `agent-latest` Desktop release in the background, provides **Check for updates** / **Update now**, and has an opt-in **Auto Update** mode. Desktop updates always upgrade Agent + Companion together through the same official bootstrap installer.

Intel Macs continue to receive the Agent-only package through the same bootstrap command until the native Companion is published for Intel Mac.

## Install / upgrade on Windows

Run this from PowerShell:

```powershell
irm https://raw.githubusercontent.com/vrassouli/MateMCP/main/scripts/bootstrap-windows.ps1 | iex
```

On Windows x64, the bootstrap automatically downloads the latest stable **MateMCP Desktop (Agent + Companion)** package, extracts it to a temporary directory, upgrades the existing installation in place, starts the background Agent and opens Companion for the interactive install, and removes the downloaded temporary files. No manual ZIP download, extraction, or previous-version uninstall is required.

Prefer a manual download instead? Use the stable Desktop asset:

- [Download MateMCP Desktop for Windows x64](https://github.com/vrassouli/MateMCP/releases/download/agent-latest/MateMCP-Desktop-win-x64.zip)
- [View the latest stable release](https://github.com/vrassouli/MateMCP/releases/tag/agent-latest)

After extracting the ZIP, run:

```powershell
.\install-windows.ps1
```

In a Desktop package that entry point installs **both Agent + Companion**. You do not need to choose between component-specific scripts.

The Agent starts at sign-in as a hidden per-user background process while the existing user-scoped Windows Credential Manager security model is retained. A migration to a real Windows Service is tracked separately. Companion does **not** auto-open at sign-in; use its **MateMCP Agent Companion** Start Menu shortcut when you want the UI. Private configuration is stored under `%APPDATA%\MateMCP`; enrolled Agent credentials and user-managed secrets are stored in Windows Credential Manager. Normal upgrades preserve configuration, credentials, and enrolled device identity.

From Companion you can monitor whether the Agent is running and Start, Stop, or Restart it without PowerShell. On Windows x64, Companion also checks the moving `agent-latest` Desktop release in the background, provides **Check for updates** / **Update now**, and has an opt-in **Auto Update** mode. Desktop updates always upgrade Agent + Companion together through the same official bootstrap installer.

Windows ARM64 continues to receive the Agent-only package through the same bootstrap command until the native Companion package is published for that architecture.

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

`main` is the source of truth for stable development. The moving `agent-latest` release contains the latest stable Agent packages plus the native Desktop packages for supported desktop architectures. Version tags such as `v0.1.0` publish versioned release assets.
