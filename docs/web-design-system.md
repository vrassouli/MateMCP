# MateMCP Web Design System

This document defines the baseline visual and interaction language for the MateMCP public website and authenticated control-plane portal.

## Design principles

1. **Calm technical confidence** — avoid noisy gradients, excessive glass effects, crowded cards, or novelty motion. Use clear hierarchy and generous whitespace.
2. **Security is visible, not theatrical** — communicate boundaries through status, scope, approval, and audit UI. Avoid fear-based security visuals.
3. **Dense when operational, spacious when explanatory** — marketing pages may breathe; portal views should keep data compact and scannable without becoming cramped.
4. **Responsive by composition** — layouts should reflow by content need, not merely shrink. Navigation, cards, tables, and actions need explicit mobile states.
5. **One spacing rhythm** — prefer the shared token scale before introducing one-off values.

## Core tokens

### Color

- Background: `#07111F`
- Elevated background: `#0B1728`
- Surface: `#0F1D30`
- Strong text: `#F4F8FF`
- Muted text: `#9DB0C9`
- Hairline border: translucent blue-gray
- Brand cyan: `#53D7FF`
- Brand blue: `#2A7BFF`
- Brand violet: `#7657FF`
- Positive: `#57D69B`
- Warning: `#F6C76E`
- Critical: `#FF7D89`

### Type

Use the native UI stack to avoid font-loading latency and privacy dependencies:

`Inter, ui-sans-serif, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif`

Marketing headings use fluid `clamp()` sizing. Operational portal text should generally stay between 13px and 16px.

### Spacing

Base scale:

- 4px
- 8px
- 12px
- 16px
- 20px
- 24px
- 32px
- 40px
- 56px
- 72px
- 96px

Primary page containers use a maximum width around 1180px with responsive side padding.

### Radius

- Small controls: 10px
- Buttons/inputs: 12px
- Cards: 18–24px
- Hero/product frames: 28px

## Responsive rules

- Wide desktop: > 1180px
- Desktop/tablet landscape: 821–1180px
- Mobile/tablet portrait: <= 820px
- Small mobile: <= 520px

At <= 820px:
- primary navigation becomes a controlled menu;
- two-column marketing sections become one column;
- action groups wrap or stack;
- architecture diagrams change from horizontal to vertical;
- decorative floating elements are reduced or removed.

At <= 520px:
- avoid side-by-side primary actions when labels become cramped;
- reduce card padding;
- keep minimum interactive target height near 44px.

## Focus and accessibility

- Never remove focus indication globally.
- Use `:focus-visible` for keyboard-focused interactive elements.
- Maintain a visible high-contrast ring separated from the component border.
- Respect `prefers-reduced-motion: reduce`.
- Decorative SVGs should be hidden from accessibility APIs.
- Icon-only controls require accessible names.

## Content rules

- Lead with the concrete outcome before implementation detail.
- Prefer short paragraphs and specific capability names.
- Security copy should explain boundaries and behavior rather than make absolute claims.
- Avoid unverified claims such as “zero risk”, “unhackable”, or blanket privacy guarantees.

## Component language

### Buttons
- Primary: one high-emphasis CTA per cluster.
- Secondary: dark elevated surface with visible border.
- Ghost: navigation/account utility action.

### Cards
- Use border + subtle surface contrast before shadow.
- Use shadows only for elevated/product-preview elements.
- Avoid nesting more than two card levels.

### Status
- Status always combines text with color; color alone is never the only signal.
- Online/healthy uses green, pending/approval uses amber, revoked/error uses red.

### Motion
- Default transitions: 160–220ms.
- Prefer opacity/transform for decorative motion.
- No essential information may depend on animation.
