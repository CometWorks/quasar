# Quasar/Components/Pages/ServersPageDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Thin MudBlazor full-screen dialog wrapper around the `<Servers>` page component, used from dashboard-style views that want to open server management without navigating away. When the user clicks a config profile link inside the embedded Servers view, the dialog closes itself and opens `ConfigsPageDialog` in its place.

## Structure
- **No `@page` route** — dialog only.
- **`[Inject]`**
  - `IDialogService DialogService`
- **`[CascadingParameter]` `IMudDialogInstance MudDialog`**
- **Key UI**
  - `MudDialog` with back arrow in `TitleContent`.
  - `<Servers ConfigProfileSelected="OpenConfigProfileAsync" />` — the full Servers page embedded in the dialog body.
  - "Done" close button.
- **`OpenConfigProfileAsync(string configProfileId)`** — closes this dialog, then opens `ConfigsPageDialog` full-screen with `InitialProfileId` set.

## Dependencies
- [`Quasar/Components/Pages/Servers.razor`](Servers.razor.md)
- `Quasar/Components/Shared/ConfigsPageDialog.razor` (referenced by type name)
- MudBlazor — `MudDialog`, `MudIconButton`, `MudButton`, `IDialogService`.
