# Quasar/Components/Pages/ConfigsPageDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Thin full-screen dialog wrapper that hosts the `Configs` page component. Used by the Home dashboard setup wizard to surface the config-template editor as a modal overlay without navigating away from the dashboard.

## Structure
- **No route** (dialog component only)
- **Cascading parameter:** `IMudDialogInstance MudDialog`
- **Parameters:**
  - `InitialProfileId` (string?) — forwarded to the embedded `<Configs>` component to pre-select a profile.
- **UI:** `MudDialog` with a back-arrow icon button + "Config Templates" title, `<Configs InitialProfileId="@InitialProfileId" />` as the dialog body, and a Done button that closes the dialog.
- `Close()` calls `MudDialog.Close()` with no result value.

## Dependencies
- [`Quasar/Components/Pages/Configs.razor`](Configs.razor.md)
- MudBlazor (`MudDialog`, `MudButton`, `MudIconButton`, `MudText`)
