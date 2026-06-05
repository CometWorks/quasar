# Quasar/Components/Pages/WorldTemplates.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page at `/world-templates` for managing reusable Space Engineers world templates. Provides an inline import form (name, description, source path with folder browser), a sortable table of existing templates with size and status, and Clone / Delete actions. Template files are stored in managed Quasar storage via `QuasarWorldTemplateCatalog`.

## Structure
- **`@page "/world-templates"`**
- **`@implements IDisposable`**
- **`[Inject]`**
  - `QuasarWorldTemplateCatalog WorldTemplateCatalog`
  - `ISnackbar Snackbar`
  - `IDialogService DialogService`
- **Key UI**
  - Left panel (xl:5) — import form: `MudTextField` for name, description, source path + "Browse" folder picker button + Import / Clear buttons.
  - Right panel (xl:7) — `MudTable<WorldTemplateRow>` with sortable columns: Name, Description, Size (MB or "missing" chip), Updated; row actions: Clone, Delete.
- **`WorldTemplateRow` (private sealed record)** — `(QuasarWorldTemplate Template, bool WorldExists, long FileSizeMb)`.
- **`Templates` computed property** — maps catalog entries to `WorldTemplateRow`, computing directory size in MB via `DirectoryInfo.GetFiles("*", AllDirectories).Sum(f.Length)`.
- **Key methods**
  - `ImportAsync` — validates name and path, calls `WorldTemplateCatalog.ImportAsync`, resets form.
  - `CloneAsync` — re-imports using the template's managed world directory as the source path.
  - `OpenFolderPickerAsync` — opens `FolderPickerDialog`.
  - `DeleteAsync` — confirms via `ShowMessageBoxAsync`, calls `WorldTemplateCatalog.DeleteAsync`.
  - `ResetForm` — clears the three form fields.

## Dependencies
- [`Quasar/Services/QuasarWorldTemplateCatalog.cs`](../../Services/QuasarWorldTemplateCatalog.cs.md)
- [`Quasar/Models/QuasarWorldTemplate.cs`](../../Models/QuasarWorldTemplate.cs.md)
- `Quasar/Components/Shared/FolderPickerDialog.razor`
- MudBlazor — `MudGrid`, `MudPaper`, `MudTable`, `MudTextField`, `MudButton`, `MudChip`, `MudAlert`, `ISnackbar`, `IDialogService`.

## Notes
- Directory size is computed inline on every render by iterating all files; this can be slow for large templates. Consider caching if templates grow large.
- The "missing" state (world directory not found) is surfaced as a warning chip and disables the Clone button, but does not auto-remove the catalog entry.
