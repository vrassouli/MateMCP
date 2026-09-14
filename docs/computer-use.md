# Computer Use end-to-end guide

MateMCP Computer Use is designed for AI clients that need to inspect and operate a real browser or desktop application, then verify the rendered result. The Agent exposes deterministic primitives; the calling AI performs semantic reasoning.

The preferred loop is:

```text
build -> run -> open -> observe -> act -> observe -> edit -> rebuild/reload -> capture -> compare -> close
```

## Choose the least fragile interaction mode

Use these modes in order:

1. **Browser semantics** for web applications. Prefer DOM role/name/label/test-id selectors, browser waits, responsive captures, and visual comparison.
2. **Native accessibility semantics** for desktop applications. Prefer `ui_snapshot` plus `ui_click` / `ui_type` / `ui_toggle` / `ui_select`.
3. **Vision-assisted isolated coordinates** when the control is visible but has no usable semantic action. Use the element/window geometry from the most recent observation and `ui_click_at` where supported.
4. **Raw mouse/keyboard fallback** only when semantic and isolated-coordinate paths cannot perform the action. Re-observe immediately after raw input.

Never silently change from a semantic operation to a coordinate action. A fallback is a separate approval-gated and auditable step.

## Frontend workflow

A representative development cycle is:

```text
shell: build/start local app
browser_open("http://127.0.0.1:5000/")
browser_wait_for(role="button", name="Management")
browser_click(role="button", name="Management")
browser_click(role="button", name="Devices")
browser_fill(role="textbox", name="Search devices", value="router")
browser_select(role="combobox", name="Device type", value="switch")
browser_snapshot()
visual_capture(preset="desktop", comparisonId="before-desktop")
visual_capture(preset="mobile", comparisonId="before-mobile")

shell: modify/rebuild application
browser_reload()
browser_wait_for(role="heading", name="Devices")
visual_capture(preset="desktop", comparisonId="after-desktop")
visual_capture(preset="mobile", comparisonId="after-mobile")
visual_compare(before="before-desktop", after="after-desktop", tolerance=2)
visual_compare(before="before-mobile", after="after-mobile", tolerance=2)
browser_close()
```

Common responsive presets are `desktop` (1440x900), `laptop` (1280x800), `tablet` (768x1024), and `mobile` (390x844). Captures include bounded DOM/geometry/style metadata in addition to screenshot pixels. Visual diff reports changed-pixel counts/percentages and changed regions; the Agent does not decide whether a change is desirable.

Prefer `browser_wait_for` to arbitrary sleeps. Visual capture can temporarily suppress animations/transitions/caret rendering and can mask small known-dynamic regions. Keep tolerance low; a large tolerance can hide a real font or color regression.

Password browser controls remain protected: snapshot values are redacted and browser fill refuses protected password fields.

## Native desktop workflow

A representative desktop cycle is:

```text
window_list()
screen_capture(target="window", id="...")
ui_snapshot(windowId="...")
ui_type(windowId="...", automationId="nameInput", text="MateMCP")
ui_click(windowId="...", automationId="incrementButton")
ui_focus(windowId="...", automationId="nameInput")
keyboard_shortcut(["CMD", "A"])       # macOS
keyboard_shortcut(["CTRL", "A"])      # Windows
keyboard_type("replacement")
ui_snapshot(windowId="...")
```

If a semantic action is unavailable, use the latest element bounds to deliberately choose a fallback. Accessibility/UIA element bounds are reported in global screen coordinates. `ui_click_at` uses **window-relative** coordinates, so convert the center point before calling it:

```text
relativeX = elementBounds.X + elementBounds.Width / 2 - window.X
relativeY = elementBounds.Y + elementBounds.Height / 2 - window.Y
ui_click_at(windowId="...", x=relativeX, y=relativeY)
```

After the application exits or restarts, treat previous window/element identifiers as stale. MateMCP rejects an unavailable window and tells the client to call `window_list` again; do not silently reuse an old identifier.

Secure/password native controls are marked `Protected=true` and expose no value. Semantic text entry refuses them.

### macOS notes

Screen pixels require Screen Recording permission. Native semantic/input actions require Accessibility permission for the process performing the actions. Synthetic shortcuts are sent as real Quartz modifier transitions; after modifier release MateMCP uses a short bounded settle so a following text action does not accidentally inherit Command/Option state. The controlled E2E scenario is exercised with the system's active keyboard layout, including non-Latin layouts.

### Windows notes

Native semantics use Microsoft UI Automation via `MateMCP.WindowsDesktopHelper.exe`. The helper is included in the Desktop package. The opt-in source field test builds/copies it automatically when necessary. UAC/secure desktop is not bypassed.

## Approvals, risk, and local stop

Computer Use approvals carry a deterministic local risk hint and expected-effect explanation:

- `Low` — observation/navigation or a non-protected field edit that does not itself submit an external operation.
- `Sensitive` — action may save/submit/send/change persistent state, or its effect is uncertain because it is raw/keyboard input.
- `High` — destructive, financial, credential/security, permission, or similarly consequential action.

Semantic approval policy targets include the action, risk, and semantic selector instead of using one blanket `semantic-action` scope. This prevents an `Always allow` decision on a benign control from automatically covering a later destructive control.

Literal typed text is omitted from approval/audit detail where the tool contract says so. Screenshot pixels are not persisted in the audit log. Browser diagnostic text is returned to the caller but not copied into audit details.

Companion exposes the active Computer Use indicator and a local Stop control. Stop creates a local sentinel checked by the Agent before future visual/input actions; it does not depend on Relay availability. Resume clears the stop state but does not resurrect the old session.

## Verification and field tests

The browser runtime workflow runs on both Chrome/macOS and Edge/Windows in CI:

```bash
dotnet test tests/MateMCP.Agent.Tests/MateMCP.Agent.Tests.csproj \
  -c Release \
  --filter "FullyQualifiedName~BrowserRuntimeIntegrationTests|FullyQualifiedName~BrowserEndToEndWorkflowTests"
```

The native GUI field test is opt-in because it needs a real signed-in desktop plus OS permissions:

```bash
MATEMCP_NATIVE_E2E=1 dotnet test tests/MateMCP.Agent.Tests/MateMCP.Agent.Tests.csproj \
  -c Release \
  --filter FullyQualifiedName~NativeComputerUseEndToEndTests
```

On Windows PowerShell:

```powershell
$env:MATEMCP_NATIVE_E2E='1'
dotnet test tests\MateMCP.Agent.Tests\MateMCP.Agent.Tests.csproj `
  -c Release `
  --filter FullyQualifiedName~NativeComputerUseEndToEndTests
```

The controlled test application is created temporarily and is stopped/removed by the test. It verifies real window discovery/capture, semantic tree/actions, secure-field redaction, keyboard shortcut/text entry, coordinate fallback, final capture, and stale-window rejection after application shutdown.

The normal test suite also contains an end-to-end security path that creates a real risk-aware pending approval, accepts it locally, verifies privacy-conscious audit output, stops Computer Use locally, verifies subsequent actions are blocked, and resumes without restoring the previous session.

## Payload discipline

Repeated observe/act loops must stay bounded. Current safeguards include capped element counts, 16 MiB screenshot limits, bounded diagnostics, short-lived visual comparison captures, and audit records that store metadata instead of image payloads. Clients should request only the viewport/element count they need and close dedicated test browsers/apps when the workflow is complete.
