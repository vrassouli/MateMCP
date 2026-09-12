# Desktop control direction

Desktop control remains independently permissioned from filesystem and shell access.

The implementation is tracked by Epic #125. Phase 1 (#126) provides read-only visual inspection; Phase 2 (#127) adds raw mouse/keyboard/window input as an explicitly approval-gated fallback; Phase 3 (#128) adds semantic accessibility/UI automation.

## Vision foundation

The Agent publishes these MCP tools on Windows and macOS:

- `screen_list` — lists displays with logical desktop bounds, primary-display state and scale factor.
- `window_list` — lists visible top-level application windows with stable-enough session identifiers and geometry.
- `screen_capture` — returns capture metadata plus an MCP `ImageContentBlock` containing a PNG that the AI client can inspect directly.

`screen_capture` accepts:

- `target=screen` with an optional `id` from `screen_list`; omitted `id` means the primary display.
- `target=window` with an `id` from `window_list`.
- `target=region` with `x`, `y`, `width`, and `height` in global desktop logical coordinates.

On HiDPI displays, the returned PNG can contain more physical pixels than the logical region size. The metadata returned before the image includes the actual PNG pixel dimensions.

Capture payloads are bounded to 16 MiB and temporary image files are deleted immediately after the image is read into the MCP result. Audit records store capture metadata only, not screenshot pixels.

## Raw desktop input

Phase 2 publishes:

- `mouse_move`
- `mouse_click`
- `mouse_drag`
- `mouse_scroll`
- `keyboard_type`
- `keyboard_press`
- `keyboard_shortcut`
- `window_focus`

Coordinates use the same global logical desktop coordinate space as visual discovery/capture. The intended loop is `observe -> act -> observe` rather than blind input.

Raw input has no reliable semantic understanding of the target control, so all Phase 2 input actions request the `desktop.input / raw-input` approval. A user may approve once or approve for the current/persistent policy according to the existing MateMCP approval model. Approval text describes the specific action even though the policy key is shared, so the first decision is understandable while session approval remains usable across subsequent coordinates/keys.

`keyboard_type` deliberately omits the actual typed string from approval/audit records. It is not a GUI secret-injection primitive and must not be used to disclose or log passwords. A future GUI secret path must resolve named secrets inside the Agent in the same non-disclosing manner as terminal secret injection.

## Semantic UI automation

Phase 3 prefers native accessibility/UI Automation semantics over coordinates.

Published tools:

- `ui_snapshot(windowId, maxElements)`
- `ui_click(windowId, role, name, automationId, parentId, index)`
- `ui_type(...)`
- `ui_focus(...)`
- `ui_toggle(...)`
- `ui_select(...)`
- `ui_expand(...)`
- `ui_scroll_into_view(...)`

A snapshot contains, where the OS exposes them, role/control type, accessible name, automation identifier, non-protected value, enabled/focused/selected/checked/expanded state, bounds, parent id, and supported native actions.

Example flow:

```text
window_list()
ui_snapshot(windowId="...")
ui_click(windowId="...", role="menuitem", name="Management")
ui_click(windowId="...", role="button", name="Devices")
ui_type(windowId="...", role="textbox", name="Search", text="MacBook3")
ui_snapshot(windowId="...")
```

Selectors are exact, case-insensitive semantic matches. Repeated controls can be disambiguated with `automationId`, `parentId`, or an explicit zero-based `index`. An ambiguous selector fails without taking action; MateMCP never silently chooses the first matching control.

Native action patterns are preferred. If a selected control does not expose the requested native pattern, the operation fails. When bounds are available, the error can expose those bounds so the AI may deliberately choose a raw coordinate tool. That fallback is a separate, approval-gated, auditable action rather than an invisible behavior change.

Protected/password controls are marked protected and their value is always returned as `null`. Semantic `ui_type` refuses protected controls instead of attempting to read or replace their value.

### Phase 3 platform status

#### Windows

Windows uses Microsoft UI Automation through the native UIA COM API. Snapshot support includes hierarchy/runtime ids where available. Native actions currently use Invoke, Value, SelectionItem, Toggle, ExpandCollapse, ScrollItem, and SetFocus patterns. Windows secure desktop/UAC boundaries are not bypassed.

#### macOS

macOS semantic snapshots and actions use the native Accessibility API through `AXUIElement` inside the Agent process and require Accessibility permission for the MateMCP Agent executable. Secure text fields are redacted/refused. Unsupported AX actions fail explicitly with bounds when available; MateMCP never silently converts them into coordinate input.

## Computer Use safety

Vision, raw input, and semantic UI actions participate in the local Computer Use session/revoke model from #130. Companion shows an active/idle/stopped indicator and can create a local stop sentinel. The Agent checks that sentinel before touching the desktop, so Stop does not depend on Relay availability. Removing the sentinel explicitly resumes future Computer Use without resurrecting the previous session.

The public Companion indicator intentionally omits session ids, target/window titles, and detailed revoke reasons. Detailed status remains private to the Agent process/user.

### Platform notes

#### macOS

Display metadata comes from CoreGraphics. Window metadata comes from Quartz Window Services and window screenshots use the built-in `screencapture` utility. MateMCP must have Screen Recording permission before pixel capture succeeds. Mouse/keyboard generation and semantic inspection require the appropriate macOS Accessibility/Input Monitoring permissions. `window_focus` activates the owning application for the selected window.

#### Windows

Display/window metadata comes from Win32 APIs. Capture runs in the signed-in user's desktop session and uses the Windows graphics stack through inbox PowerShell/.NET desktop assemblies. The implementation explicitly uses per-monitor DPI awareness so screenshot coordinates stay aligned with discovery results. Input uses the signed-in interactive desktop; Windows secure desktop/UAC boundaries are not bypassed.

Window capture in the Phase 1 implementation represents the currently rendered window bounds. Minimized Windows windows must be restored before capture.

## Capability roadmap

- screen capture / visual inspection — Phase 1 (#126)
- mouse / keyboard / window focus — Phase 2 (#127)
- semantic accessibility/UI automation — Phase 3 (#128)
- browser/DOM automation — Phase 4 (#129)
- responsive and before/after visual QA — Phase 5 (#131)
- cross-cutting risk/session/revoke UX — #130
- clipboard access — future scoped capability, independently permissioned

Preferred interaction order:

1. Browser semantics when operating a web UI
2. Native semantic accessibility/UI automation
3. Screenshot + vision fallback
4. Raw mouse/keyboard coordinates

## Security requirements

- separate policy capabilities for visual inspection, raw input, semantic input, and future clipboard access
- obvious local indicator while AI computer use is active
- immediate local pause/kill control
- sensitive OS permission prompts and credential surfaces remain approval-gated
- every action is auditable
- screenshot pixels are not retained by default
- literal keyboard text is not written to audit logs
- password/secure-text contents are never exposed through visual/semantic snapshots
