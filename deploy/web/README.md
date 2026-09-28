# MateMCP public Web deployment

`MateMCP.Web` serves the public product website for `https://matemcp.com`. It is intentionally deployed separately from the authenticated control plane at `https://api.matemcp.com`.

## Container

The published image is:

```text
vrassouli/matemcp-web:latest
```

The container listens on port `8082`. The installer defaults the host bind address to `127.0.0.1`; when the HTTPS reverse proxy runs on another machine, set `MATEMCP_WEB_BIND_INPUT` to the private/LAN address that proxy can reach.

Example for a reverse proxy on another host:

```bash
curl -fsSL https://raw.githubusercontent.com/vrassouli/MateMCP/main/deploy/web/install.sh |
  sudo MATEMCP_WEB_BIND_INPUT=192.168.200.38 MATEMCP_WEB_PORT_INPUT=8082 bash
```

Normal updates preserve `/opt/matemcp-web/.env`, pull the latest image, recreate the container, and verify `/health`.

## Reverse-proxy routing

Keep the public surfaces independent:

| Public hostname | Backend |
| --- | --- |
| `matemcp.com` | MateMCP Web, port `8082` |
| `api.matemcp.com` | MateMCP API, port `8081` |
| `relay.matemcp.com` | MateMCP Relay, port `8080` |

Terminate HTTPS at the edge/reverse proxy and forward the original `Host` plus `X-Forwarded-Proto: https`. Do not route `matemcp.com` to the API, Relay, or an unrelated virtual host.

If Cloudflare proxies the hostnames, use an authenticated/valid origin TLS configuration (prefer Full (strict)) and avoid cache rules that cache authenticated API responses.

## Verification

After routing is live:

```bash
curl -fsS https://matemcp.com/health
curl -fsS https://api.matemcp.com/health
curl -fsS https://relay.matemcp.com/health
```

Also verify:

- `https://matemcp.com/` serves the public MateMCP product page and its canonical/OG metadata references `matemcp.com`.
- `https://api.matemcp.com/login` and `/register` render the account portal.
- registration/login, Devices, Approvals, and Admin authorization work through the public hostname.
- HSTS/CSP/nosniff/referrer/permissions headers are present where expected.
- no container port is exposed directly to the public Internet; access should arrive through the reverse proxy/firewall path.
