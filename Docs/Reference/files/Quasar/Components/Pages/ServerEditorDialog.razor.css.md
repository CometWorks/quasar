# Quasar/Components/Pages/ServerEditorDialog.razor.css

**Module:** Quasar.Components  **Kind:** CSS  **Tier:** 3

## Summary
Scoped stylesheet for `ServerEditorDialog.razor`. Constrains the dialog's scrollable content area, styles the template-select input slots with subtle background tints and focus highlights, and restores the controlled read-only implicit-Magnetar-mod checkbox's icon color without recoloring its label. Previously named `ServerEditorDialog.razor.css`.

## Structure
- `.server-editor-dialog-content` — `max-height: min(75vh, 56rem); overflow-y: auto; padding-right: 0.25rem;` — makes the form body scrollable within the dialog bounds.
- `.server-editor-template-select` — flex basis `18rem`, `min-width: 16rem`; used for config/world template `MudSelect` wrappers.
- `.server-editor-template-select ::deep .mud-input-slot` — 2% text-primary background tint, `border-radius` from MudBlazor variable, 0.15 s transition.
- `.server-editor-template-select:hover ::deep .mud-input-slot` — 4% tint on hover.
- `.server-editor-template-select:focus-within ::deep .mud-input-slot` — 8% primary tint + 1 px inset primary box-shadow on keyboard/pointer focus.
- `.server-editor-controlled-checkbox ::deep .mud-checkbox.mud-readonly` — keeps the pointer cursor for the read-only checkbox used when the dialog must control the actual setting transition.
- `.server-editor-controlled-checkbox ::deep .mud-checkbox.mud-readonly .mud-button-root`, `.mud-icon-root` — force only the checkbox button/icon color to `--server-editor-controlled-checkbox-color`, which is set from the same local MudBlazor `Color` passed to the checkbox component.
- `.server-editor-controlled-checkbox ::deep .mud-checkbox.mud-readonly .mud-checkbox-input` — keeps the pointer cursor over the underlying native checkbox input.

## Dependencies
- [`Quasar/Components/Pages/ServerEditorDialog.razor`](ServerEditorDialog.razor.md) (scoped to this component)
- MudBlazor CSS custom properties (`--mud-palette-text-primary-rgb`, `--mud-palette-primary-rgb`, `--mud-default-borderradius`)
