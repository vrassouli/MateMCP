# Local credential injection

MateMCP resolves named credentials inside the Agent process. An AI client can request a credential by name, but `ICredentialStore.ResolveAsync()` reads its value from the local operating-system credential store only after local approval succeeds. The resolved value is never placed in the MCP tool response.

The injection flow is:

1. `shell_session_send_secret` identifies the existing PTY/ConPTY session and named credential.
2. The Agent verifies that the credential policy explicitly allows `shell_session_send_secret`.
3. A per-credential sliding-window limit rejects excessive injection attempts and records a security violation.
4. The Agent requests local approval scoped to that credential and command fingerprint.
5. `ICredentialStore.ResolveAsync()` obtains the secret locally.
6. `InteractiveShellSessionManager.WriteSecretAsync()` writes the value directly to the PTY/ConPTY stdin stream.
7. The interactive process continues, while terminal echo is redacted and the audit log records only credential name, tool, command fingerprint, and outcome.

Consequently, the AI sees the credential name, allowed-tool and approval/result metadata, and redacted terminal output, but not the credential value. Existing credential metadata created before allowed-tool policies is migrated safely to the only supported injection tool, `shell_session_send_secret`; newly saved credentials carry an explicit allowed-tool list.

The local management UI includes a credential usage viewer backed by the loopback-only `/credential-audit` endpoint. It never reads from the operating-system credential value store.

The integration harness uses a real cross-platform .NET console process to verify prompt detection, local resolution, stdin injection, process continuation, redaction, failure handling, and cleanup. CI also starts an ephemeral Linux OpenSSH server with a randomly generated, masked password and validates the complete SSH password authentication path through the real `ssh` client and PTY.
