# Native macOS semantic UI actions

MateMCP uses the native macOS Accessibility API (AXUIElement) for semantic desktop actions. The existing `ui_snapshot` accessibility tree is used to select one unique element before any action is performed.

Supported macOS actions:

- `ui_focus` sets native AX focus.
- `ui_type` sets the native AX value and refuses secure/password fields.
- `ui_click` and `ui_toggle` require native `AXPress`.
- `ui_select` prefers `AXPress` and otherwise uses the writable selected accessibility property.
- `ui_expand` uses the writable expanded accessibility property.
- `ui_scroll_into_view` requires native `AXScrollToVisible`.

MateMCP never silently falls back to screen coordinates. When a native semantic action is unavailable, the operation fails and reports element bounds when available so raw input can be chosen explicitly.

Accessibility permission for the MateMCP Agent executable is required on macOS. Semantic inspection/actions run directly through `AXUIElement`; `/usr/bin/osascript` does not need Accessibility permission. Raw CGEvent input uses the same Agent Accessibility trust preflight. Typed text is omitted from approval and audit details, and secure-text values remain redacted.
