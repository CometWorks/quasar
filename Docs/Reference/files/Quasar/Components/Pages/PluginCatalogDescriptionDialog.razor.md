# Quasar/Components/Pages/PluginCatalogDescriptionDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Small read-only MudBlazor dialog that shows a plugin catalog entry's full description. The title displays the plugin display name with the plugin id as a caption, and the body renders the description text with preserved line breaks; a single primary button closes it.

## Structure
- **Dialog:** `MudDialog` with `TitleContent` (display name + optional mono `PluginId` caption), `DialogContent` (`MudText` with `white-space: pre-wrap`), and `DialogActions` (Close button).
- **`[CascadingParameter]`:** `IMudDialogInstance MudDialog` — used to close the dialog.
- **`[Parameter]`s:**
  - `DisplayName` (string) — friendly plugin name, dialog title.
  - `PluginId` (string) — catalog plugin id, shown as a caption when non-empty.
  - `Description` (string) — full description, rendered with preserved whitespace.
- **Methods:** `Close()` calls `MudDialog.Close()`.

## Dependencies
- MudBlazor (`MudDialog`, `MudStack`, `MudText`, `MudButton`, `IMudDialogInstance`)

## Notes
- Newly added dialog for the plugin catalog flow; purely presentational (no service injection, no result payload).
