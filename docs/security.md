# v0.1 threat model

MateMCP v0.1 is intentionally powerful software. The user explicitly grants an AI client access to selected local capabilities.

## Trust boundaries

- The local user is the authority.
- The MCP client is authenticated but not implicitly trusted with every local resource.
- Configured project roots define pre-authorized filesystem/shell scopes.
- MCP Roots are discovery hints only and are never used as authorization boundaries.
- Requests outside configured scopes must be denied or routed through explicit local approval.

## Credentials

On macOS, the local MCP bearer credential and the enrolled Agent credential are stored in **macOS Keychain** under the `MateMCP.Agent` service. New installations do not write the local access token to `appsettings.json`.

At startup, an existing plaintext `Mate:AccessToken` is migrated into Keychain and removed from the JSON configuration. An explicit `MATEMCP_Mate__AccessToken` environment override remains supported for automation/testing and is never written back to disk.

Credential values must never be written to normal logs, audit records, status responses, or the local management UI.

## Direct exposure

The v0.1 transport is designed for deliberate router/firewall port forwarding. Public exposure requires HTTPS and bearer authentication. The bearer token must be treated as a secret and must never appear in normal logs.

Safe first-run defaults bind only to loopback over HTTP. Public HTTP is not an acceptable production configuration. Local management and approval endpoints are loopback-only even when the MCP endpoint is otherwise exposed.

## Project configuration

The local management UI can add, edit, and remove project roots and read/write/shell permissions without hand-editing configuration files. Project configuration is non-secret and is persisted with user-only file permissions where supported. The Agent reloads project definitions from configuration changes.

## Filesystem

All project-relative paths are canonicalized before access. Path traversal and symbolic-link escapes outside the configured root are rejected. Destructive operations must remain distinguishable for policy/audit purposes.

## Shell

Shell commands run with the permissions of the MateMCP process and therefore can be more powerful than filesystem tools. Project shell permission is separate from read/write permission. Known high-value API credentials are removed from the child environment; future policy work must expand secret filtering and approval semantics.

## Audit

Filesystem and shell invocations produce local audit records. Audit output must be bounded and must avoid storing bearer credentials or other known secrets.

## Future desktop control

Screen viewing, mouse control, keyboard input, clipboard, and Accessibility automation are separate capabilities. They require visible local control state and an immediate local pause/kill mechanism.
