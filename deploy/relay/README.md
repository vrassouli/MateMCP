# MateMCP Relay deployment

## Single-instance requirement

The Relay currently owns Agent WebSocket connections in an in-process `AgentRegistry`. A connected Agent is therefore reachable only from the Relay process that owns that WebSocket.

The production Compose deployment is intentionally single-instance (`container_name: matemcp-relay`). Do not scale the Relay service to multiple containers/processes or add multiple Relay backends behind a load balancer until distributed Agent connection ownership/routing is implemented. Ordinary HTTP sticky sessions are not sufficient because an Agent WebSocket and an independent MCP client request can originate from different clients.

Relay startup, `/health`, MCP response headers, Agent connection lifecycle logs, and `device_offline` diagnostics include a Relay instance identity. `MATEMCP_RELAY_INSTANCE_ID` can be set to a stable non-sensitive deployment identifier; if omitted, Relay generates a process-unique identity.

When investigating `device_offline`, compare the Relay instance identity and Agent connection/generation ID around register, replace, remove, stale-remove, disconnect, and MCP request events before assuming the Agent itself is down.

## Per-Agent backpressure

Relay bounds pending MCP work for each connected Agent so a slow Agent or a burst of concurrent AI sessions cannot grow the in-memory pending request set without limit. The default capacity is 32 pending requests per Agent connection and can be changed with `MATEMCP_RELAY_MAX_PENDING_REQUESTS_PER_AGENT` (Relay clamps configured values to 1..4096).

When the capacity is full, existing in-flight requests continue normally and only new work is rejected immediately with HTTP `429`, `Retry-After: 1`, and `X-MateMCP-Backpressure: agent_busy`. The response also exposes `X-MateMCP-Agent-Pending` and `X-MateMCP-Agent-Capacity` for diagnostics. Relay logs the device, connection, session, operation, pending count, and configured capacity for each backpressure rejection.

Capacity is reclaimed when a request completes, times out, is cancelled, or its Agent transport is lost. A replacement Agent connection gets its own fresh bounded admission state; operation/session recovery remains independent of the physical connection generation.

## Graceful restart and drain

The Compose deployment gives Relay a 30-second container stop grace period. On SIGTERM/shutdown, Relay first marks all current Agent connections as draining. Already-admitted MCP requests are allowed to finish for up to 15 seconds by default, while new work receives HTTP `503` with `error=relay_draining`, `Retry-After: 2`, and `X-MateMCP-Relay-State: draining`.

The drain window can be changed with `MATEMCP_RELAY_SHUTDOWN_DRAIN_SECONDS`; Relay clamps it to 0..120 seconds. Keep the Docker/orchestrator termination grace comfortably larger than the configured drain window. The checked-in Compose default is intentionally 30 seconds for the default 15-second drain.

After all pending requests finish, or when the drain window expires, Relay deliberately disconnects its Agent WebSockets and logs the drain outcome and remaining pending count. The Agent reconnect/backoff logic then binds to the replacement Relay process. This makes planned deploys distinguishable from unexpected transport loss, but it does not make in-memory Relay state survive a process crash.

For routine Docker Compose updates, the existing command remains appropriate because Compose honors `stop_grace_period` while replacing the old container:

```bash
docker compose pull
docker compose up -d --force-recreate --remove-orphans
```

## Reverse proxy / Nginx

`nginx.example.conf` contains a production-oriented baseline for the Relay. The important transport assumptions are:

- `/relay/agent/` must support WebSocket upgrade and HTTP/1.1;
- proxy read/send timeouts should exceed normal Relay request timeout and tolerate idle periods around WebSocket keepalive traffic;
- `/mcp/` should not be response-buffered, and request buffering can be disabled so the proxy does not add another large buffering layer;
- upstream keepalive should be enabled for ordinary HTTP traffic;
- the proxy should preserve `Host`, `X-Forwarded-For`, and `X-Forwarded-Proto`.

The sample uses 180-second proxy read/send timeouts, which is deliberately above the current 120-second Relay request timeout. If `MATEMCP_RELAY_REQUEST_TIMEOUT_SECONDS` is increased, review the proxy timeouts at the same time rather than treating either value in isolation.
