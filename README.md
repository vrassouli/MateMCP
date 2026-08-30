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

## macOS install

On Apple Silicon and Intel Macs, install or upgrade MateMCP Agent with one command:

```bash
curl -fsSL https://raw.githubusercontent.com/vrassouli/MateMCP/feat/relay-mvp/scripts/bootstrap-macos.sh | bash
```

The bootstrap detects `arm64` vs `x86_64`, downloads the matching package from the latest successful `feat/relay-mvp` CI build, and runs the packaged installer. Existing user configuration is preserved during upgrades.

Then start MateMCP with:

```bash
matemcp
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

## Relay server

The Relay image is published to Docker Hub as `vrassouli/matemcp-relay`. The current development branch publishes the `dev` tag for both `linux/amd64` and `linux/arm64`.

On a fresh Debian/Ubuntu VPS, install and start Relay with one command:

```bash
curl -fsSL https://raw.githubusercontent.com/vrassouli/MateMCP/feat/relay-mvp/deploy/relay/install.sh | sudo bash
```

The installer:

- installs Docker Engine and the Docker Compose plugin when needed;
- downloads `deploy/relay/docker-compose.yml` from the same Git ref;
- creates `/opt/matemcp-relay/.env` with random Agent and Client tokens;
- pulls `vrassouli/matemcp-relay:dev` from Docker Hub;
- starts the Relay with `docker compose`;
- checks `http://127.0.0.1:8080/health` before finishing.

The generated credentials are printed once at initial installation and remain stored in `/opt/matemcp-relay/.env` with mode `600`.

To update the running Relay later:

```bash
cd /opt/matemcp-relay
docker compose pull
docker compose up -d
```

By default Relay listens on `0.0.0.0:8080`. Do not expose port `8080` directly to the Internet. Put it behind the intended reverse proxy/firewall and expose only HTTPS (normally `443`) externally.

For development/testing, the installers use the `feat/relay-mvp` Git ref and the Relay uses Docker image tag `dev`. After Relay is merged to `main`, the stable install commands and default image tag should be switched to `main`/`latest`.
