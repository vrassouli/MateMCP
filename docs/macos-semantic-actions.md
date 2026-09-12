# Native macOS semantic UI actions

MateMCP uses macOS Accessibility through System Events/JXA for semantic desktop actions. The existing `ui_snapshot` accessibility tree is used to select one unique element before any action is performed.

Supported macOS actions:

- `ui_focus` sets native AX focus.
- `ui_type` sets the native AX value and refuses secure/password fields.
- `ui_click` and `ui_toggle` require native `AXPress`.
- `ui_select` prefers `AXPress` and otherwise uses the writable selected accessibility property.
- `ui_expand` uses the writable expanded accessibility property.
- `ui_scroll_into_view` requires native `AXScrollToVisible`.

MateMCP never silently falls back to screen coordinates. When a native semantic action is unavailable, the operation fails and reports element bounds when available so raw input can be chosen explicitly.

Accessibility permission for MateMCP is required on macOS. Typed text is omitted from approval and audit details, and secure-text values remain redacted.
