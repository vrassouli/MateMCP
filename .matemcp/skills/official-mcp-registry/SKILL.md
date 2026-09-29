---
name: official-mcp-registry
description: Publish and maintain MateMCP metadata in the Official MCP Registry without exposing registry credentials.
---

# Official MCP Registry

Use this skill when changing or publishing MateMCP's Official MCP Registry listing.

## Identity and metadata

- The current registry name is `io.github.vrassouli/matemcp`.
- `server.json` at the repository root is the source of truth.
- MateMCP is a remote Streamable HTTP server whose endpoint is per Agent:
  `https://relay.matemcp.com/mcp/{agent_id}`.
- Keep `agent_id` as a required remote URL template variable. Never publish a real user's `agt_...` identifier in registry metadata.
- Keep the product positioning concise and truthful; the current lead is `Your agent hit a limit? Keep working.`

## Authentication

- Publishing uses the Official MCP Registry's GitHub Actions OIDC flow.
- The registry workflow needs `id-token: write` and authenticates with `mcp-publisher login github-oidc`.
- No registry private key, PAT, or MCP Registry token belongs in Git, workflow inputs, logs, Issues, or AI-visible output.
- If MateMCP later migrates to a domain namespace such as `com.matemcp/*`, treat that as a deliberate listing migration and use the registry's supported domain-verification flow.

## Publish flow

1. Update `server.json`.
2. Push to `main`; `.github/workflows/mcp-registry.yml` validates metadata from a GitHub-hosted runner.
3. Confirm validation succeeds.
4. Manually dispatch the `mcp-registry` workflow with `publish=true`.
5. Confirm the workflow publishes successfully and verify the listing through the Official MCP Registry API/site.

The local Mac/VHD networks may be unable to reach `registry.modelcontextprotocol.io`; prefer the GitHub-hosted workflow rather than weakening network or credential handling.
