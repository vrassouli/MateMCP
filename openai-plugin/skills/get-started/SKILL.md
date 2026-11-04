---
name: matemcp-get-started
description: Connect to the selected MateMCP Agent, verify which computer this connection represents, and use its projects within the granted local permissions.
---

# Get started with MateMCP

## Understand this connection

1. Call `get_profile` first. It identifies the Agent that the user selected during OAuth authorization. Treat the returned id, name, email and nickname as the identity of this connection.
2. Never ask the model or user to switch this connection by passing an Agent id to a tool. Agent selection happens in the MateMCP OAuth authorization page.
3. If the user needs a different computer or Agent, ask them to add another MateMCP connection in the host application and select the desired Agent during that connection's authorization flow. Existing connections remain independent.
4. If the profile is not the expected Agent, stop and ask the user to verify the connected account/connection in the host settings.

## Discover permitted workspaces

1. Use `project_list` to discover the projects configured on this Agent.
2. Read each project's configured permissions and availability before acting.
3. Use `filesystem_list`, `filesystem_stat` and `filesystem_read` only within the selected project's permitted root. Respect bounded reads and the Agent's path checks.
4. Do not invent a project name or attempt to access files outside configured project roots.

## Approval and safety

- The Agent's scopes, local project permissions, operating-system permissions and approval policy determine what can be done. A tool being available does not mean an action is automatically approved.
- For changes, commands, browser interaction or desktop input, explain the intended effect and follow the approval response. Never try to bypass an approval requirement.
- Do not request or expose passwords, tokens or secret values. Use MateMCP's named secret-fill tools where a credential is needed; the Agent resolves values locally.
- Never attempt to enumerate or switch to another user's Agent.

## Connection troubleshooting

- If `get_profile` is unavailable, check that the server connection completed OAuth against `https://relay.matemcp.com/mcp`.
- If Agent authorization fails, ask the user to check that the Agent is active and that the OAuth connection was created under the account that owns it.
- To connect another Agent, create a separate connection instead of changing tool arguments.
