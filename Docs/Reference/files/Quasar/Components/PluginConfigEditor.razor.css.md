# Quasar/Components/PluginConfigEditor.razor.css

**Module:** Quasar.Components  **Kind:** CSS  **Tier:** 3

## Summary
Scoped CSS for `PluginConfigEditor.razor`. Provides layout constraints and visual distinction for config containers, composite value editors (vectors, structs, poses), list/dict rows, and key fields.

## Structure
- `.plugin-config-container` — `min-width: 0` (prevents overflow in flex contexts).
- `.plugin-config-field` — `min-width: 0` (same, per-field wrapper).
- `.plugin-config-composite`, `.plugin-config-nested`, `.plugin-config-list-row` — shared card-like appearance: `background: var(--mud-palette-background-gray)`, `border: 1px solid var(--mud-palette-lines-default)`, rounded corners (`--mud-default-borderradius + 2px`). Distinguish nested values (structs, vectors) from the surrounding form.
- `.plugin-config-row-index` — fixed 2 rem width, right-aligned; the item number in a list row.
- `.plugin-config-row-editor` — `flex: 1 1 auto`, `min-width: min(18rem, 100%)`; the field editor in a list/dict row.
- `.plugin-config-key-field` — `flex: 0 1 16rem`, `min-width: min(12rem, 100%)`; the key input in a dict row.

## Dependencies
- MudBlazor CSS custom properties (`--mud-palette-background-gray`, `--mud-palette-lines-default`, `--mud-default-borderradius`).
