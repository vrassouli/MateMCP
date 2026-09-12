# Desktop control direction

Desktop control remains independently permissioned from filesystem and shell access.

The implementation is tracked by Epic #125. The first foundation is the read-only visual inspection surface from #126.

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

### Platform notes

#### macOS

Display metadata comes from CoreGraphics. Window metadata comes from Quartz Window Services and window screenshots use the built-in `screencapture` utility. MateMCP must have the OS Screen Recording permission before pixel capture succeeds. When permission is missing, the tool returns an actionable error rather than bypassing the OS privacy boundary.

#### Windows

Display/window metadata comes from Win32 APIs. Capture runs in the signed-in user's desktop session and uses the Windows graphics stack through the inbox PowerShell/.NET desktop assemblies. The implementation explicitly uses per-monitor DPI awareness so screenshot coordinates stay aligned with discovery results.

Window capture in the Phase 1 implementation represents the currently rendered window bounds. Minimized Windows windows must be restored before capture. Later semantic/native automation phases can add richer off-screen/native capture strategies where useful.

## Planned capability groups

- screen capture / visual inspection — Phase 1 (#126)
- mouse move / click / scroll — Phase 2 (#127)
- keyboard text / key / hotkey — Phase 2 (#127)
- semantic accessibility/UI automation — Phase 3 (#128)
- browser/DOM automation — Phase 4 (#129)
- responsive and before/after visual QA — Phase 5 (#131)
- clipboard access — future scoped capability, independently permissioned

Preferred interaction order:

1. Browser semantics when operating a web UI
2. Native semantic accessibility/UI automation
3. Screenshot + vision fallback
4. Raw mouse/keyboard coordinates

## Security requirements

- separate grants for screen viewing, mouse control, keyboard input, and clipboard
- obvious local indicator while AI computer use is active
- immediate local pause/kill control
- sensitive OS permission prompts and credential surfaces remain approval-gated
- every action is auditable
- password/secure-text contents are never exposed through visual/semantic snapshots

The cross-cutting approval, session-indicator and emergency-stop work is tracked in #130.
