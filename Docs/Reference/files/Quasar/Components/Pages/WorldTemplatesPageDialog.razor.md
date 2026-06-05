# Quasar/Components/Pages/WorldTemplatesPageDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Thin MudBlazor dialog wrapper around the `<WorldTemplates>` page component, used from dashboard-style views to open world template management without a full navigation. No additional logic beyond hosting the component and providing a back-arrow close button.

## Structure
- **No `@page` route** — dialog only.
- **`[CascadingParameter]` `IMudDialogInstance MudDialog`**
- **Key UI**
  - `MudDialog` with back-arrow `MudIconButton` in `TitleContent` and "Done" close button in actions.
  - `<WorldTemplates />` embedded in `DialogContent`.
- **`Close`** — calls `MudDialog.Close()`.

## Dependencies
- [`Quasar/Components/Pages/WorldTemplates.razor`](WorldTemplates.razor.md)
- MudBlazor — `MudDialog`, `MudIconButton`, `MudButton`.
