# ChatGPT MCP tool refresh after Agent updates

MateMCP Agent can gain or change MCP tools during an upgrade, but an existing ChatGPT app connection may continue to use an older approved tool snapshot.

OpenAI documents that ChatGPT keeps a frozen snapshot of an approved MCP app's tools and inputs. MCP server updates are not enabled automatically. Enterprise/Edu admins can use the app's Action control / Configure Actions **Refresh** flow to review updated actions; Business workspaces currently need to recreate and republish a published app when its tools or metadata change.

Official reference: https://help.openai.com/en/articles/12584461

## Detect a stale ChatGPT tool snapshot

The local Agent status endpoint is loopback-only:

`http://127.0.0.1:45871/status`

Its `mcpTools` section reports:

- `count`: number of tools exposed by the running Agent;
- `revision`: a short fingerprint derived from tool names and input signatures;
- `names`: the live tool names.

If the live Agent lists a tool but the existing ChatGPT app does not expose it, the Agent installation is not missing that capability. Refresh or recreate the ChatGPT app action snapshot instead of reinstalling the Agent repeatedly.

For the structured SSH credential flow, current Agent builds should expose at least:

- `ssh_session_start`
- `ssh_session_authenticate`
- `shell_session_read`
- `shell_session_write`
- `shell_session_close`

The generalized `shell_session_send_secret` action remains available for non-SSH interactive prompts and backwards compatibility.

## Refresh the ChatGPT side

Where your workspace exposes action refresh controls:

1. Open Workspace settings → Apps.
2. Locate the MateMCP app/device connection.
3. Open Action control or Configure Actions.
4. Choose **Refresh**.
5. Review and enable the newly discovered or changed actions.

If your plan/workspace does not offer Refresh for an already published app, recreate/re-publish the app using the same current MateMCP MCP endpoint and complete OAuth again as required by ChatGPT.

Afterward, start a new chat or reselect/@mention the refreshed app so the current action set is available to the conversation.

## Distinguish a stale snapshot from an Agent-side denial

A stale snapshot means ChatGPT does not expose a tool that the live Agent reports in `mcpTools.names`.

A host-side rejection means the tool exists in ChatGPT but a particular call is stopped before it reaches MateMCP. In that case there will be no corresponding Agent audit event for the attempted operation. If the request reaches MateMCP and MateMCP denies it, the Agent audit should record the specific approval, tool-policy, rate-limit, or other Agent-side decision.

Never copy local access tokens or secret values into ChatGPT while troubleshooting. Credential names and non-secret tool metadata are sufficient for this comparison.
