# Quasar/wwwroot/app.css

**Module:** Quasar.Host  **Kind:** CSS  **Tier:** 3

## Summary
Global stylesheet for the Quasar Blazor Server UI. Overrides MudBlazor's elevation shadows with a flatter, lower-opacity variant; establishes base layout styles; and defines application-specific utility and component classes that complement the MudBlazor theme.

## Structure

**`:root` custom properties** — Redefines all 25 MudBlazor `--mud-elevation-N` shadow values with lighter `rgba(0,0,0,0.1/0.07/0.06)` shadows (flatter than MudBlazor defaults). A separate `min-width: 1280px` block also overrides MudBlazor typography sizes (h4–h6, subtitle1/2, body1/2, caption, button).

**Base reset** — `html, body` margin 0, min-height 100%; body uses MudBlazor palette CSS variables for background and primary text colour; Roboto/Helvetica/Arial font stack. `h1:focus` outline removed.

**Layout classes:**
- `.magnetar-appbar` — bottom border using `--mud-palette-lines-default`
- `.magnetar-appbar-subtitle`, `.magnetar-nav-section` — secondary text colour
- `.magnetar-body` — full-height content area, responsive `padding-inline` with `clamp`
- `.magnetar-shell-content` — block padding with `clamp`
- `.magnetar-nav-menu .mud-nav-link` — rounded corners + spacing; `.active` state forces a primary-tinted background with `!important` (to beat MudBlazor's higher-specificity rule), primary colour, bold weight, and tints the link icon

**Config template tiles** (kept in global CSS because scoped CSS doesn't reliably reach MudBlazor components):
- `.mud-paper.config-template-tile` — gray background with colour/background transitions
- `.config-template-selected` — primary-tinted background (`!important`)

**Card classes:**
- `.summary-card` — flat bordered surface card (no shadow), with `.summary-card-warning` / `.summary-card-error` rgba-tint + border variants
- `.server-card` — flat surface card for server panels; `.mud-table-container` inherits border radius
- `.servers-list-table` — forces the embedded server table to keep a minimum table width inside a horizontally scrollable MudBlazor table container, used when the dashboard list view disables MudBlazor's responsive row/card layout

**MudBlazor overrides:**
- `.mud-expansion-panels > .mud-expand-panel` — flat bordered panels with `0.5rem` gap, hover/focus header highlight, expanded-header bottom border
- `.mud-checkbox` — rounded hit area with hover/focus background highlight
- `.mud-table-hover .mud-table-body .mud-table-row:not(.servers-list-detail-row):hover` — paints hovered table rows with theme primary colour, forces row descendants and button roots to primary contrast text, switches standalone SVG icons and outlined borders to the same contrast colour, keeps success/warning/error buttons and chips on semantic mixed hover colours, lets button-owned icons inherit from MudBlazor's button root colour, and skips expanded server detail rows
- `.mud-main-content`, `.mud-drawer` / `.magnetar-drawer` — background and right-border styling

**Utility / feature classes:**
- `.mono` — JetBrains Mono / Cascadia Code monospace font; `.mud-typography-caption.mono` renders monospace ID captions at 50% opacity (`opacity: 0.5`)
- `.copyable-path`, `.copyable-path-inline`, `.copyable-path-text`, `.copyable-path-button` — shared layout and monospace wrapping for `CopyablePath` path labels and clipboard icon buttons
- `.chat-list` / `.chat-row` — scrollable chat log column with row separators
- `.chat-console-card`, `.chat-server-select`, `.admin-chat-list`, `.admin-chat-row` — full-page chat console sizing, server-select minimum width, and bounded scrollable chat rows for `Chat.razor`
- `.players-list-card`, `.players-list-stack`, `.players-table`, and descendant table selectors — force the known-player table stack and MudBlazor table/container to consume full available width
- `.world-template-browse-button` — 1rem top margin
- `.installed-world-template-table`, `.installed-world-template-name-cell`, `.installed-world-template-source-cell`, `.installed-world-template-action-cell`, `.installed-world-template-name-text`, `.installed-world-template-source-stack`, `.installed-world-template-source-text` — fixed-layout predefined-world tables where the left Add action stays in a fixed action column and long source/category text ellipsizes instead of forcing horizontal overflow
- `.branding-logo-preview` (+ `-dark`, `-light`) and `.branding-favicon-preview` — bordered preview containers for logo/favicon images

**Responsive adjustments:**
- `max-width: 959.98px` — reduce body and content padding
- `min-width: 1280px` — compact typography, paper/card/table/button/input padding, summary cards, heading margins

**Blazor boilerplate** — `.blazor-error-boundary` (inline SVG icon + "An error has occurred." `::after`); `.darker-border-checkbox`; floating-label placeholder alignment rules.

## Dependencies
- MudBlazor CSS variables (`--mud-palette-*`, `--mud-elevation-*`, `--mud-default-borderradius`, `--mud-typography-*`)
