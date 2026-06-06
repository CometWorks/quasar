# Quasar/Components/Pages/Configs.razor.css

**Module:** Quasar.Components  **Kind:** CSS  **Tier:** 3

## Summary
Scoped stylesheet for `Configs.razor`. Styles the two-column page shell (sticky template sidebar + scrollable editor), the template tiles, collapsible config/world panels, option cards (with search-highlight and focus states), workshop thumbnails, and plugin description lines.

## Structure
- **Page shell / layout:** `.configs-page-shell`, `.configs-sidebar` (+ `-inner`, `-template-list`), `.configs-main-column`, `.configs-editor-scroll` — flex/scroll containers; on `min-width: 1280px` the shell is `100vh`-bounded and the sidebar becomes sticky; on `max-width: 599px` columns revert to auto height.
- **Template tiles:** `.config-template-tile`, `.config-template-row`, `.config-template-button` (+ `-selected`, `::deep .mud-button-label`), `.config-template-delete`. (The persistent selected highlight `.config-template-selected` lives in global `app.css`, per the file comment.)
- **Plugins/mods:** `.plugin-actions`, `.plugin-description-line` (now wraps normally — `white-space: normal; overflow-wrap: anywhere;` — instead of single-line ellipsis, since the catalog shows the short Tooltip text inline and moves the full description to its own icon-button column; the former `.plugin-description-cell` / `-more` rules were removed), `.workshop-thumbnail` (80x80 preview), `.configs-search-field`, `.configs-add-button`.
- **Panels/options:** `.configs-expansion-panels`, `.configs-world-panels-shell`, `.configs-expansion-panel` (+ header hover), `.config-option-card` with state modifiers `.config-option-highlight` (search match), `.config-option-first` (first match), `.config-option-focus`; `.config-selection-row`, `.config-secondary` (dimmed text).

## Dependencies
- [`Quasar/Components/Pages/Configs.razor`](Configs.razor.md) (consumes these classes).
- Relies on MudBlazor CSS variables (`--mud-palette-*`, `--mud-default-borderradius`) and a global `app.css` class for the selected-tile highlight.
