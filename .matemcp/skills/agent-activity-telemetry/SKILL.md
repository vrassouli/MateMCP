---
name: agent-activity-telemetry
description: Preserve MateMCP Agent activity visibility without leaking command payloads or destabilizing the MCP request path.
---

# Agent activity telemetry

Use this skill when changing Agent online/activity visibility, Relay runtime telemetry, or the portal's recent-activity view.

## Storage and retention

- Runtime Agent activity is stored in the existing API `AuditEvents` stream with event types under `runtime.*` while the API still uses the current `EnsureCreated` schema flow.
- Keep runtime telemetry logically separate from security/audit history by prefix. Do not prune enrollment, revoke, approval, credential, or admin audit records just to enforce the runtime history limit.
- Retain the newest 500 runtime events per Agent. Use the monotonic audit `Id` for bounded newest-first queries so the SQLite regression path does not depend on `DateTimeOffset` ordering translation.

## Privacy boundary

- Relay telemetry may record the JSON-RPC method and, for `tools/call`, the tool name.
- Never persist MCP arguments, command text, request bodies, response bodies, credentials, tokens, approval secrets, environment values, file contents, or raw exception payloads as runtime activity.
- Record generic outcome messages, status, duration, and a bounded request/correlation identifier instead.
- The API must sanitize and bound text again before persistence even when the Relay already emits safe fields. Treat this as defense in depth.
- Portal rendering must HTML-encode activity fields and remain scoped to the authenticated device owner.

## Reliability

- Activity reporting is best-effort and must never block or materially delay the MCP request path.
- Use a bounded asynchronous queue between Relay request handling and control-plane activity writes. When pressure exceeds the queue limit, dropping old telemetry is preferable to stalling Agent work.
- Telemetry delivery failures must not fail an otherwise-valid Agent request.

## Verification

- Keep tests proving tool-call arguments and secrets never enter the activity description.
- Keep API integration coverage for internal-key enforcement, redaction, owner-scoped display/filtering, and the 500-event runtime retention limit.
- When changing the portal view, retain responsive activity-filter/list coverage and perform browser QA when practical.
