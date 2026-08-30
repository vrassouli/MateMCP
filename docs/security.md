# v0.1 threat model

MateMCP v0.1 is intentionally powerful software. The user explicitly grants an AI client access to selected local capabilities.

## Trust boundaries

- The local user is the authority.
- The MCP client is authenticated but not implicitly trusted with every local resource.
- Configured project roots define pre-authorized filesystem/shell scopes.
- MCP Roots are discovery hints only and are never used as authorization boundaries.
- Requests outside configured scopes must be denied or routed through explicit local approval.

## Direct exposure

The v0.1 transport is designed for deliberate router/firewall port forwarding. Public exposure requires HTTPS and bearer authentication. The bearer token must be treated as a secret and must never appear in normal logs.

Safe first-run defaults bind only to loopback over HTTP. Public HTTP is not an acceptable production configuration.

## Filesystem

All project-relative paths are canonicalized before access. Path traversal and symbolic-link escapes outside the configured root are rejected. Destructive operations must remain distinguishable for policy/audit purposes.

## Shell

Shell commands run with the permissions of the MateMCP process and therefore can be more powerful than filesystem tools. Project shell permission is separate from read/write permission. Known high-value API credentials are removed from the child environment; future policy work must expand secret filtering and approval semantics.

## Audit

Filesystem and shell invocations produce local audit records. Audit output must be bounded and must avoid storing bearer credentials or other known secrets.

## Future desktop control

Screen viewing, mouse control, keyboard input, clipboard, and Accessibility automation are separate capabilities. They require visible local control state and an immediate local pause/kill mechanism.
