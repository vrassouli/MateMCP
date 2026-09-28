# Production web surfaces

MateMCP exposes three independent public hostnames. Do not collapse them onto one backend.

| Hostname | Component | Default backend port |
| --- | --- | ---: |
| `matemcp.com` | Public product website (`MateMCP.Web`) | 8082 |
| `api.matemcp.com` | Account, OAuth, control plane (`MateMCP.Api`) | 8081 |
| `relay.matemcp.com` | MCP/Agent relay (`MateMCP.Relay`) | 8080 |

## Delivery

The canonical single-server installer updates Web, API, and Relay:

```bash
curl -fsSL https://raw.githubusercontent.com/vrassouli/MateMCP/main/deploy/install.sh | sudo bash
```

Component installers remain available under `deploy/web`, `deploy/api`, and `deploy/relay`.

The public Web image is `vrassouli/matemcp-web:latest`. The API and Relay images remain `vrassouli/matemcp-api:latest` and `vrassouli/matemcp-relay:latest`.

## Reverse proxy and TLS

Terminate public HTTPS at the reverse proxy/edge and route each hostname to its own backend. Forward the original host and HTTPS scheme. If Cloudflare is in front of the origin, prefer Full (strict) TLS and do not cache authenticated API responses.

Container ports are private backend ports. Bind them to loopback when the reverse proxy is on the same host; when the reverse proxy is on another trusted LAN host, bind only to the private address it can reach and use firewall policy to prevent direct public access.

## Production configuration

API production state is persistent and security-sensitive:

- keep `/opt/matemcp-api/.env` mode 0600;
- preserve the API data volume across updates;
- keep the API/Relay internal key synchronized through the canonical installer;
- configure a bootstrap administrator through deployment secrets/environment on the initial deployment or promote the configured matching account with the idempotent bootstrap behavior;
- never put bootstrap passwords, internal keys, database credentials, external-login provider secrets, or Agent credentials in Git or deployment documentation.

External login providers are optional and independently configurable. See [External login providers](external-login-providers.md) for provider credentials, callback URLs, account-linking behavior, and the production verification checklist.

The Web service has no private application secrets.

## Release verification

A release is not complete until all of the following pass on the public hostnames:

```bash
curl -fsS https://matemcp.com/health
curl -fsS https://api.matemcp.com/health
curl -fsS https://relay.matemcp.com/health
```

Verify response headers for the public website and account portal, including HSTS, CSP, `X-Content-Type-Options`, referrer policy, and permissions policy.

Then verify in a real browser:

1. `matemcp.com` renders the product website and has the expected canonical/OG metadata.
2. Register and sign in through `api.matemcp.com`.
3. Enroll/manage a device and confirm revoked devices leave the active list.
4. Exercise an approval Allow/Deny round trip.
5. Confirm a regular user receives 403 on `/admin`.
6. Confirm an administrator can search/manage users without secret material appearing in markup.
7. Check mobile (~390 px), intermediate (~900 px), and desktop (~1440 px) layouts.
8. Check browser diagnostics for unexpected console/network errors.

Do not close a production-delivery issue merely because images were built; verify the actual public routes and deployed image/container state.
