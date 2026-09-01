# Interactive shell test harness

The automated Agent test project uses a fake interactive terminal command instead of a real SSH server so CI can exercise PTY/ConPTY behavior deterministically on both macOS and Windows.

The harness covers:

- starting a long-lived interactive process
- incremental output reads with cursors
- ordinary input writes
- secret injection and output redaction
- process exit
- invalid session ids
- credential-not-found and denied credential use
- orphan/idle cleanup and maximum session lifetime
- concurrent session limits
- cancellation
- bounded output buffers
- verification that the secret value is absent from MCP-tool responses, captured terminal output, and audit logs

For a real end-to-end smoke test, store a credential on an Agent and run the SSH workflow in `docs/interactive-shell-secrets.md`. A suitable test target should permit password authentication and should not contain production data.
