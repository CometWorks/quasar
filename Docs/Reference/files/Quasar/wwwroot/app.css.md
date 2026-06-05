# Quasar/wwwroot/app.css

**Module:** Quasar.Host  **Kind:** CSS  **Tier:** 3

## Summary
Global stylesheet for the Quasar Blazor Server UI. Overrides MudBlazor's elevation shadows with a flatter, lower-opacity variant; establishes base layout styles; and defines application-specific utility and component classes that complement the MudBlazor theme.

## Structure

**`:root` custom properties** — Redefines all 25 MudBlazor `--mud-elevation-N` shadow values with lighter `rgba(0,0,0,0.1/0.07/0.06)` shadows (flatter than MudBlazor defaults). Also overrides MudBlazor typography sizes at `min-width: 1280px` (h4–h6, subtitle, body, caption, button).

**Base reset** — `html, body` margin 0, min-height 100%; body uses MudBlazor palette CSS variables for background and primary text colour; Roboto/Helvetica/Arial font stack.

**Layout classes:**
- `.magnetar-appbar` — bottom border using `--mud-palette-lines-default`
- `.magnetar-appbar-subtitle`, `.magnetar-nav-section` — secondary text colour
- `.magnetar-body` — full-height content area, responsive `padding-inline` with `clamp`
- `.magnetar-shell-content` — block padding with `clamp`
- `.magnetar-nav-menu .mud-nav-link` — rounded corners + spacing; active state uses `--mud-palette-action-default-hover`
- `.magnetar-drawer`, `.mud-drawer` — right border + drawer background colour

**Card classes:**
- `.summary-card` — flat bordered surface card (no shadow), with warning/error variants (`.summary-card-warning`, `.summary-card-error`) using rgba tint + border colour
- `.server-card` — flat surface card for server panels

**MudBlazor overrides:**
- `.mud-expansion-panels > .mud-expand-panel` — flat bordered panels with `0.5rem` gap, hover/focus highlight, expanded header border
- `.mud-checkbox` — hover/focus background highlight

**Utility classes:**
- `.mono` — JetBrains Mono / Cascadia Code monospace font
- `.chat-list` / `.chat-row` — scrollable chat log column with row separators
- `.world-template-browse-button` — 1rem top margin

**Branding preview classes:**
- `.branding-logo-preview` (+ `-dark`, `-light`) — bordered flex container for logo image preview
- `.branding-favicon-preview` — 64px square preview container

**Responsive adjustments:**
- `max-width: 959.98px` — reduce body and content padding
- `min-width: 1280px` — compact typography, padding, table cells, buttons, inputs, summary cards

**Blazor boilerplate** — `.blazor-error-boundary` error display; `.darker-border-checkbox`; floating label placeholder alignment.

## Dependencies
- MudBlazor CSS variables (`--mud-palette-*`, `--mud-elevation-*`, `--mud-default-borderradius`, `--mud-typography-*`)
