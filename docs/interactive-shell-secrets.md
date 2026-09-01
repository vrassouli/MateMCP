# Interactive shell sessions and named credentials

MateMCP supports long-lived interactive shell sessions for commands that require terminal input, including SSH, sudo, database CLIs, installers, and other TTY-aware programs.

## AI-facing MCP tools

- `shell_session_start(command, project?)` starts a command under a real PTY/ConPTY and returns `sessionId`, `processId`, initial terminal output, `nextOffset`, state, and exit code when available.
- `shell_session_read(sessionId, offset)` returns output produced since the supplied cursor. Pass the previous `nextOffset` to receive only new output. `outputTruncated=true` means older buffered output was discarded.
- `shell_session_write(sessionId, text, submit)` writes ordinary non-secret terminal input.
- `shell_session_send_secret(sessionId, credential, submit)` resolves a named credential only inside the Agent and writes it directly to the PTY/ConPTY.
- `shell_session_close(sessionId)` terminates and removes the session.
- `secret_list()` exposes only safe metadata: credential name, type, and description.

Cursor-based polling is used instead of server-pushed streaming because the current MateMCP MCP path is stateless HTTP through Relay. No process state or secret value is stored by Relay/API.

## Complete SSH example

Assume the user has stored a credential named `server-admin-password` on the Agent.

1. AI calls:

   `shell_session_start(command: "ssh administrator@192.168.200.38")`

2. Agent returns a `sessionId` and terminal output. The command remains alive inside a PTY/ConPTY.

3. AI calls `shell_session_read` until it sees output such as:

   `administrator@192.168.200.38's password:`

4. AI decides that this prompt needs the saved credential and calls:

   `shell_session_send_secret(sessionId: "...", credential: "server-admin-password")`

5. The Agent asks for the configured `secret.use` approval if needed, resolves the value from the local secure credential store, and writes the bytes directly to the running terminal. The secret value is never returned to the AI and never becomes an MCP argument.

6. AI continues calling `shell_session_read` with the returned cursor and can interact with the SSH session using `shell_session_write`.

7. When finished, AI calls `shell_session_close`.

MateMCP deliberately does **not** detect password prompts itself. The AI observes terminal output and chooses when a specific named credential should be injected into a specific running session.

## Credential storage

The Agent exposes an `ICredentialStore` abstraction. The current implementation stores values in:

- macOS: Keychain
- Windows: Credential Manager

The metadata index contains only name, description, type, and timestamps. Supported MVP metadata types are `Password`, `Token`, `SshPassphrase`, and `Generic`; the type is descriptive and does not change how the raw secret is delivered to the terminal.

Linux PTY operation is supported by the terminal layer, but persistent named-secret storage is not yet implemented for Linux.

## Security boundaries

- Existing one-shot `shell_exec` is unchanged and remains the preferred path for non-interactive commands.
- Interactive commands run under PTY on Unix/macOS and ConPTY on Windows.
- Secret values are resolved only by the Agent. Relay, API, MCP clients, and the AI see only credential identifiers/metadata.
- Ordinary input and secret injection are separate MCP operations.
- Every credential injection is audited by credential name/reference and session id, never by value.
- `secret.use` persistent/session approvals are scoped to the credential **and an exact command fingerprint**, so approving a credential for one SSH command does not grant reusable injection into arbitrary unrelated commands.
- Secret values are registered as output redactions before bytes are written, including protection when echoed text is split across terminal read chunks.
- Common ambient API/cloud secrets are removed from the child process environment.
- Session ids are high-entropy random capabilities local to one Agent. Relay routes requests to one Agent only after its existing owner/agent authorization checks, so sessions cannot cross devices; possession of the unpredictable session id is additionally required for follow-up operations.
- The Agent enforces configurable maximum concurrent sessions, inactivity TTL, maximum lifetime, input size, and output buffer size. Expired/orphaned processes are killed and removed.
- Relay classifies all `shell_session_*` operations as `mcp:shell`; `secret_list` remains `mcp:read`. This prevents interactive execution or credential injection from being authorized by read-only OAuth scope.

Default limits are:

- maximum sessions: 8
- inactivity timeout: 10 minutes
- maximum lifetime: 1 hour
- output buffer: 500,000 characters
- one input operation: 65,536 characters

They can be overridden under `Mate:InteractiveShell` in Agent configuration.

## Relay/API update requirements

The wire protocol itself remains unchanged: interactive operations are ordinary MCP tool calls, and all process/PTY state plus secret resolution remain Agent-local. The **API/Control Plane does not require an update** for this feature.

The **Relay does require the authorization update that ships with this feature**. Relay already forwards arbitrary MCP tool calls generically, but its OAuth scope classifier must explicitly require `mcp:shell` for:

- `shell_session_start`
- `shell_session_read`
- `shell_session_write`
- `shell_session_send_secret`
- `shell_session_close`

Without that mapping, new tool names would fall through to the default `mcp:read` scope. This is an authorization issue, not a transport/state requirement.

The runtime flow remains:

1. Relay verifies user/Agent/resource ownership and requires `mcp:shell` for interactive shell calls.
2. Relay forwards the MCP call to the selected Agent.
3. The Agent owns the process and keeps all PTY state locally.
4. Follow-up calls contain only `sessionId`, cursor, ordinary input, or a credential **identifier**.
5. Credential value resolution and injection happen entirely inside that Agent; Relay and API never receive the value.
