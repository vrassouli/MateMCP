# MateMCP — OpenAI plugin submission package

This folder contains the portable Agent Plugins package for the MateMCP public OpenAI directory listing. It is deliberately separate from the existing Official MCP Registry `server.json` entry and connects to the new stable endpoint `https://relay.matemcp.com/mcp`. Existing per-Agent URLs remain supported.

## Package contents

- `plugin.json`: listing metadata, onboarding skill reference and exactly five positive / three negative MCP review scenarios.
- `mcp.json`: remote Streamable HTTP MCP endpoint. OAuth is explicitly configured as Dynamic Client Registration (DCR) in the OpenAI MCP server extension. The API authorization-server discovery document supplies the registration endpoint; no client ID, client secret, or other credential is embedded in the package.
- `skills/get-started/SKILL.md`: instructions for validating the selected Agent, handling multiple connections and staying within local permissions.
- `assets/`: the existing square MateMCP brand mark, used for the listing logo and composer icon.

## Security

The package contains no client secret, API key, Agent credential, test account or reviewer login. DCR is used for the OpenAI-hosted connection. Reviewer access, where requested by the submission dashboard, must be entered through its secure credential form and must never be committed here.

## Before submitting

1. Deploy the API, Relay and website changes and verify the public stable endpoint and `/privacy`, `/terms` and `/support` URLs over HTTPS.
2. Complete live OAuth authorization against the production domain with at least two Agents owned by one test account; verify independent connections, profile binding, revocation, disabled-account and legacy per-Agent behavior.
3. Run the eight review scenarios in a dedicated test account with sanitized sample projects and record the actual observed tool calls/results. The manifest describes expected cases; it is not evidence that the OpenAI review walkthrough has already been executed.
4. Produce and host a reviewer-accessible demo recording; add its real URL to `extensions.com.openai.review.demo_recording_url` before initial MCP review.
5. Verify the domain through the OpenAI submission flow and complete the publisher/developer identity checks in the dashboard. Do not invent or pre-fill a domain verification challenge token.
6. Confirm the legal operator/publisher identity, jurisdiction, privacy contact, data retention/deletion process and any applicable Google Analytics consent requirements before treating the policy pages as final legal advice.

## Local validation

Run node scripts/validate-openai-plugin.mjs to check package metadata, assets, onboarding references, review cases, and OAuth/DCR configuration. The portable MCP fields follow Agent Plugins 1.0.0; the server-level extensions.com.openai.auth block is the documented OpenAI-specific addition.
