---
name: web-product-design
description: Keep MateMCP public website and account portal visually coherent, responsive, accessible, and product-grade.
---

# MateMCP web product design

Use this skill when changing `matemcp.com`, the account portal, or other product-facing web UI.

## Source of truth

Read `docs/web-design-system.md` before making visual changes.

## Rules

- Preserve the calm dark technical visual language: deep navy surfaces, restrained cyan/blue/violet brand accents, subtle borders, and limited shadows.
- Use the documented spacing/type/radius tokens before inventing one-off values.
- Responsive behavior must be compositional. Verify at mobile (~390 px), mid-size/laptop (~900 px), and wide desktop (~1440 px).
- Keep interactive targets around 44 px minimum where practical.
- Never remove keyboard focus globally; use a visible `:focus-visible` treatment.
- Respect `prefers-reduced-motion`.
- Security messaging must describe actual boundaries and controls; avoid absolute or unverified security/privacy claims.
- Public marketing pages can be spacious. Operational portal pages should be denser and easier to scan while remaining visually consistent.
- Prefer inline/original SVG icons and CSS product visuals over decorative stock imagery.
- Perform real browser QA and check console diagnostics before considering web UI complete.
- Add or update automated smoke tests for critical metadata, headers, and primary page content when the public surface changes.
