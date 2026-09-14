# Browser visual QA and before/after comparison

MateMCP can capture deterministic browser states at standard responsive viewport sizes and compare two captures without embedding an AI model in the Agent. The Agent provides pixels, DOM/geometry metadata, and deterministic diff metrics; the calling AI decides whether a visual change is intended or a regression.

## Tools

`visual_viewports` returns the standard presets:

| Preset | CSS viewport |
| --- | --- |
| `desktop` | 1440 x 900 |
| `laptop` | 1280 x 800 |
| `tablet` | 768 x 1024 |
| `mobile` | 390 x 844 |

`visual_capture` applies one preset, or explicit `width` + `height`, waits for fonts and two animation frames, optionally waits an additional `settleMs`, then returns:

- the PNG screenshot as an MCP image;
- a short-lived capture id;
- CSS viewport and actual PNG dimensions;
- URL/title;
- a bounded DOM/semantic snapshot with element roles, accessible names, bounds, selected computed styles, and redacted form values;
- element count/truncation information.

By default the capture temporarily disables CSS animations/transitions, hides the text caret, and disables smooth scrolling. The temporary style is removed immediately after the capture. `maskCss` can hide up to 32 known-dynamic regions without collapsing their layout, for example a live clock or ad slot. Invalid or unstable application state should preferably be handled with `browser_wait_for` before capture instead of a long fixed delay.

`visual_compare(beforeId, afterId, tolerance)` decodes both PNGs to RGBA and compares actual pixels. It reports:

- whether the images are comparable;
- explicit size mismatch information instead of silently resizing;
- changed pixel count and percentage;
- connected changed regions, aggregated on deterministic 16 x 16 tiles;
- the per-channel tolerance used for noise suppression.

Tolerance is an integer from 0 to 255 and defaults to 8. A pixel is considered changed when any RGBA channel differs by more than the tolerance. Use `0` for exact rendering tests; use a small non-zero value to avoid letting anti-aliasing noise dominate a comparison.

## Complete local frontend regression workflow

A typical AI-assisted frontend workflow is:

1. Start the local development server with the normal project command, for example `dotnet run`, `npm run dev`, or the repository-specific launcher.
2. Call `browser_open` with the local URL such as `http://127.0.0.1:5000/admin`.
3. Use `browser_wait_for` for the page-specific ready condition rather than assuming that `document.readyState` means the application is visually finished.
4. Call `visual_capture(preset: "desktop")`, `visual_capture(preset: "tablet")`, and `visual_capture(preset: "mobile")`. Keep the returned capture ids as the baseline set.
5. Inspect each capture's screenshot plus DOM bounds/styles. The calling AI can reason about overflow, clipping, wrapping, alignment, spacing, component dimensions, font-size changes, and off-screen controls from this combined evidence.
6. Modify the frontend code and rebuild/reload the application.
7. Repeat the same `browser_wait_for` and `visual_capture` calls using the same presets and masking options.
8. Call `visual_compare` for each matching before/after pair. A size mismatch usually means the wrong viewport/preset was used; changed regions identify where deterministic rendering changed.
9. Inspect only the reported changed regions and nearby DOM elements when possible, then decide whether the change is expected or a regression.
10. Close the dedicated browser with `browser_close` when the workflow is complete.

Example conceptual sequence:

```text
browser_open("http://127.0.0.1:5000/admin")
browser_wait_for(role="heading", name="Administration")

beforeDesktop = visual_capture(preset="desktop")
beforeTablet  = visual_capture(preset="tablet")
beforeMobile  = visual_capture(preset="mobile")

# edit source, rebuild/reload
browser_reload()
browser_wait_for(role="heading", name="Administration")

afterDesktop = visual_capture(preset="desktop")
afterTablet  = visual_capture(preset="tablet")
afterMobile  = visual_capture(preset="mobile")

visual_compare(beforeDesktop.id, afterDesktop.id, tolerance=8)
visual_compare(beforeTablet.id,  afterTablet.id,  tolerance=8)
visual_compare(beforeMobile.id,  afterMobile.id,  tolerance=8)
```

## Stabilization and masking guidance

Prefer deterministic application state over large tolerances. Freeze or mock network-fed clocks/random data in the development build when practical. Use `browser_wait_for` for asynchronous content, and use `maskCss` only for small regions that are intentionally non-deterministic. Mask selectors are temporary and apply only to the capture. Large tolerances can hide genuine font/color regressions, so increase tolerance only when there is measured rendering noise.

The default animation suppression covers CSS animations, transitions, caret blinking, and smooth scrolling. It does not stop JavaScript timers, video, canvas animation, remote content updates, or application code that mutates the DOM. Those sources should be stabilized by the app/test environment or masked explicitly.

## Storage, privacy, and limits

Visual captures are held only in Agent process memory for before/after comparison. They are not written to disk by the visual-QA feature. A capture expires after 30 minutes; the store is also bounded by capture count and approximately 128 MiB of retained PNG + decoded pixel data, with oldest captures evicted first.

The normal Computer Use approval/session controls still apply. Capture and compare are classified as low-risk visual inspection. DOM snapshots keep the existing password/secure-field redaction behavior. Audit entries contain capture/diff metadata and counts, not screenshot pixels or masked selector text.

PNG decoding is intentionally bounded. Visual QA accepts the non-interlaced 8-bit truecolor/truecolor-alpha PNGs emitted by supported Chromium screenshot capture and refuses unsupported or excessively large images rather than allocating unbounded memory.

## Platform/runtime coverage

The browser runtime CI executes the same semantic browser flow on Chrome/macOS and Edge/Windows. It performs a real `visual_capture`, verifies an identical self-comparison has zero changed pixels, changes page content, captures again, and verifies that `visual_compare` reports changed pixels and at least one changed region.
