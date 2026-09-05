# MateMCP Relay deployment

## Single-instance requirement

The Relay currently owns Agent WebSocket connections in an in-process `AgentRegistry`. A connected Agent is therefore reachable only from the Relay process that owns that WebSocket.

The production Compose deployment is intentionally single-instance (`container_name: matemcp-relay`). Do not scale the Relay service to multiple containers/processes or add multiple Relay backends behind a load balancer until distributed Agent connection ownership/routing is implemented. Ordinary HTTP sticky sessions are not sufficient because an Agent WebSocket and an independent MCP client request can originate from different clients.

Relay startup, `/health`, MCP response headers, Agent connection lifecycle logs, and `device_offline` diagnostics include a Relay instance identity. `MATEMCP_RELAY_INSTANCE_ID` can be set to a stable non-sensitive deployment identifier; if omitted, Relay generates a process-unique identity.

When investigating `device_offline`, compare the Relay instance identity and Agent connection/generation ID around register, replace, remove, stale-remove, disconnect, and MCP request events before assuming the Agent itself is down.
