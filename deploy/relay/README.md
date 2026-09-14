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
