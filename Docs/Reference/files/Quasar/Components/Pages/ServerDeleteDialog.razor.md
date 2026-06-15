# Quasar/Components/Pages/ServerDeleteDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
MudBlazor dialog that confirms deletion of a server definition or reports that the definition was removed while leaving the server folder on disk. It shows the affected slug and folder path, lets the user copy the folder path, and closes with `DialogResult.Ok(true)` when the destructive or acknowledgement action is accepted.

## Structure
- **No `@page` route** — dialog only; launched by server-management workflows.
- **`[CascadingParameter]` `IMudDialogInstance MudDialog`**
- **`[Parameter]`s**
  - `Slug` — server identifier shown in confirmation text.
  - `FolderPath` — server folder shown and copied.
  - `Confirm` — toggles between confirmation mode and post-delete acknowledgement mode.
- **Key UI**
  - Conditional body text for pre-delete confirmation or post-delete acknowledgement.
  - Folder path row rendered through `CopyablePath`.
  - Informational alerts explaining that Quasar does not delete the server folder and that recreating the slug reuses leftover files unless the folder is moved or deleted.
  - Cancel/Delete actions in confirmation mode; Close action in acknowledgement mode.
- **`Accept`** — closes with `DialogResult.Ok(true)`.
- **`Cancel`** — cancels the dialog.

## Dependencies
- [`Quasar/Components/Shared/CopyablePath.razor`](../Shared/CopyablePath.razor.md)
- MudBlazor — `MudDialog`, `MudStack`, `MudText`, `MudAlert`, `MudButton`.
