# Quasar/Components/Pages/WorldTemplateQuickImportDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
MudBlazor dialog for importing a Space Engineers save folder as a world template without leaving the server editor. Validates a name and absolute source path, opens a `FolderPickerDialog` for path browsing, calls `WorldTemplateCatalog.ImportAsync`, and returns the created `QuasarWorldTemplate` via `DialogResult.Ok`.

## Structure
- **No `@page` route** — dialog only; launched from `ServerEditorDialog`.
- **`[Inject]`**
  - `QuasarWorldTemplateCatalog WorldTemplates`
  - `ISnackbar Snackbar`
  - `IDialogService DialogService`
- **`[CascadingParameter]` `IMudDialogInstance MudDialog`**
- **Key UI**
  - `MudForm` with `@bind-IsValid`.
  - Name field (required, auto-focus), Description field (multi-line, optional).
  - Source world path text field + "Browse" button that opens `FolderPickerDialog`.
  - Cancel / "Import World Template" buttons; Import button shows "Importing..." while `_importing` is true.
- **`OpenFolderPickerAsync`** — opens `FolderPickerDialog` with `InitialPath = _sourcePath`, applies selected path on non-cancelled result.
- **`ImportAsync`** — validates the form, sets `_importing = true`, calls `WorldTemplates.ImportAsync`, closes with `Ok(template)` on success or shows a snackbar error.

## Dependencies
- [`Quasar/Services/QuasarWorldTemplateCatalog.cs`](../../Services/QuasarWorldTemplateCatalog.cs.md)
- `Quasar/Components/Shared/FolderPickerDialog.razor`
- [`Quasar/Models/QuasarWorldTemplate.cs`](../../Models/QuasarWorldTemplate.cs.md)
- MudBlazor — `MudDialog`, `MudForm`, `MudTextField`, `MudButton`, `ISnackbar`, `IDialogService`.
