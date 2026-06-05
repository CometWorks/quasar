# Quasar/Components/Pages/ConfigProfilePendingChangesDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Small confirmation dialog displayed by `Configs.razor` when the user tries to switch config templates while the current template has unsaved edits. Returns a `PendingChangesAction` discriminated union (Cancel, Discard, Save) to let the caller decide how to handle the pending state.

## Structure
- **No route** (dialog component only)
- **Cascading parameter:** `IMudDialogInstance MudDialog`
- **UI:** `MudDialog` with a warning alert and three action buttons — Cancel, Discard, Save — each closing the dialog with the corresponding `PendingChangesAction` enum value wrapped in `DialogResult.Ok(...)`.
- **Nested enum:** `PendingChangesAction { Cancel, Discard, Save }`
- No injected services or component parameters beyond the cascading dialog instance.

## Dependencies
- [`Quasar/Components/Pages/Configs.razor`](Configs.razor.md) (caller)
- MudBlazor (`MudDialog`, `MudAlert`, `MudButton`)
