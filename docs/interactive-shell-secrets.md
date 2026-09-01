# Interactive shell sessions and named secrets

MateMCP supports long-lived interactive shell sessions for commands that require terminal input, including SSH, sudo, database CLIs, installers, and other TTY-aware programs.

## AI-facing flow

1. Call `shell_session_start` with a command.
2. Inspect the returned terminal output and `sessionId`.
3. Call `shell_session_read` with the previous `nextOffset` to retrieve later output.
4. When ordinary input is needed, call `shell_session_write`.
5. When a locally configured credential is needed, call `shell_session_send_secret` with the credential name. The secret value is resolved locally and written directly to the running PTY/ConPTY. It is never returned to the AI.
6. Call `shell_session_close` when finished.

`secret_list` exposes only credential names and descriptions.

## Local credential management

The Agent UI has a **Secrets & credentials** section. Secret values are stored in macOS Keychain or Windows Credential Manager. The metadata index contains only names, descriptions, and timestamps.

Linux named-secret storage is not implemented yet; interactive PTY sessions themselves remain cross-platform.

## Security properties

- Existing one-shot `shell_exec` remains unchanged.
- Interactive commands run in a real PTY on Linux/macOS and ConPTY on Windows.
- Secret use is a separate MCP operation from ordinary terminal input.
- Every secret injection goes through the local `secret.use` approval flow and is audited by credential reference and session id, never by value.
- Secret values are registered as output redactions before injection to reduce accidental terminal echo exposure.
- Sessions expire after ten minutes of inactivity and are terminated on disposal.
- Common ambient cloud/API credentials are removed from the child terminal environment.

## Why MateMCP does not detect password prompts

MateMCP intentionally does not infer whether terminal output is asking for a password. The AI observes terminal output and decides when a specific named credential should be injected into a specific running session. MateMCP acts as the terminal transport and secret broker, not as a prompt classifier.
