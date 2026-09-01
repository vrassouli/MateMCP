# Local credential injection

MateMCP resolves named credentials inside the Agent process. An AI client can request a credential by name, but `ICredentialStore.ResolveAsync()` reads its value from the local operating-system credential store only after local approval succeeds. The resolved value is never placed in the MCP tool response.

The injection flow is:

1. `shell_session_send_secret` identifies the existing PTY/ConPTY session and named credential.
2. The Agent requests local approval for that credential and command fingerprint.
3. `ICredentialStore.ResolveAsync()` obtains the secret locally.
4. `InteractiveShellSessionManager.WriteSecretAsync()` writes the value directly to the PTY/ConPTY stdin stream.
5. The interactive process continues, while terminal echo is redacted and the audit log records only credential metadata and the command fingerprint.

Consequently, the AI sees the credential name, approval/result metadata, and redacted terminal output, but not the credential value. The integration harness uses a real cross-platform .NET console process to verify prompt detection, local resolution, stdin injection, process continuation, redaction, failure handling, and cleanup.
