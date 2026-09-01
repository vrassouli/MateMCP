# Agent Platform Feature Parity

MateMCP ships one Agent application for macOS and Windows. Supported Agent capabilities must remain aligned across both platforms unless this document records a concrete technical limitation and fallback.

## Current capability matrix

| Capability | macOS | Windows | Verification |
| --- | --- | --- | --- |
| Streamable HTTP MCP endpoint | Supported | Supported | Agent build and native package smoke test |
| Loopback management UI and root redirect | Supported | Supported | Native package smoke test checks `/health`, `/`, and `/ui` |
| Projects and approval management | Supported | Supported | Shared Agent routes and tests |
| Named secret management | macOS Keychain | Windows Credential Manager | Platform credential-store integration test |
| Credential injection policy and audit | Supported | Supported | Shared Agent integration tests |
| Install from stable release channel | `agent-latest` | `agent-latest` | Delivery parity regression test |
| Start after install | Per-user LaunchAgent | User process | Delivery parity regression test |
| Start on sign-in | Per-user LaunchAgent | Per-user startup shortcut | Delivery parity regression test |

## Change checklist

For every Agent-facing change:

1. Identify whether it affects shared code, OS integration, installer/bootstrap behavior, or release packaging.
2. Implement the capability in shared code where possible.
3. Add or update automated coverage for both platforms. For secure storage, exercise the real platform store without exposing values.
4. Keep macOS and Windows bootstrap scripts on the same release channel.
5. Verify the installed package starts the new binary and exposes `/health`, `/`, `/ui`, and any new local route.
6. Confirm both macOS architectures (`osx-arm64`, `osx-x64`) and both Windows architectures (`win-x64`, `win-arm64`) produce installable artifacts. Execute package smoke tests where the CI host can run the target architecture; cross-published artifacts still require successful publish and archive steps.
7. Document any unavoidable exception here with its reason, user impact, and fallback.

An Issue about platform parity remains open until the relevant build/package workflows are green and the installable artifacts have been produced.
