# Quasar/Components/Pages/WorldTemplates.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page at `/world-templates` for managing reusable Space Engineers world templates. Provides an inline import form (name, description, source path with folder browser), and a sortable table of existing templates showing size and a missing-world indicator with Clone and Delete actions. Template world files are copied into managed Quasar storage via `QuasarWorldTemplateCatalog`.

## Structure
- **`@page "/world-templates"`**, **`@implements IDisposable`**
- **`[Inject]`:** `QuasarWorldTemplateCatalog WorldTemplateCatalog`, `ISnackbar Snackbar`, `IDialogService DialogService`
- **Key UI**
  - Left panel (xl:5) — import form: `MudTextField` for name, description, and source path with a "Browse" folder-picker button, plus Import (shows "Importing…" while `_importing`) and Clear buttons.
  - Right panel (xl:7) — `MudTable<WorldTemplateRow>` with sortable columns Name, Description, Size (MB or a "missing" warning chip), Updated; row actions Clone (disabled when world missing or importing) and Delete.
- **`WorldTemplateRow` (private sealed record)** — `(QuasarWorldTemplate Template, bool WorldExists, long FileSizeMb)`.
- **`Templates` computed property** — maps catalog entries to rows, computing the on-disk world directory size in MB by summing all file lengths (`DirectoryInfo.GetFiles("*", AllDirectories)`).
- **Key methods**
  - `ImportAsync` — validates name and source path are present, calls `WorldTemplateCatalog.ImportAsync(name, description, sourcePath)`, then resets the form.
  - `CloneAsync` — re-imports using the template's managed world directory as the source, naming it "<name> (Copy)".
  - `OpenFolderPickerAsync` — opens `FolderPickerDialog` seeded with the current source path and stores the picked path.
  - `DeleteAsync` — confirms via `ShowMessageBoxAsync`, then `WorldTemplateCatalog.DeleteAsync`.
  - `ResetForm`, `HandleChanged`.
- Subscribes to `WorldTemplateCatalog.Changed` in `OnInitialized`, releases in `Dispose`.

## Dependencies
- [`Quasar/Services/QuasarWorldTemplateCatalog.cs`](../../Services/QuasarWorldTemplateCatalog.cs.md) — import/delete, world directory resolution
- `Quasar/Models/QuasarWorldTemplate.cs` — `QuasarWorldTemplate`
- `Quasar/Components/Shared/FolderPickerDialog.razor`
- MudBlazor — `MudGrid`, `MudPaper`, `MudTable`, `MudTextField`, `MudButton`, `MudChip`, `MudAlert`, `ISnackbar`, `IDialogService`

## Notes
- Directory size is recomputed inline on every render by walking all files, which can be slow for large templates.
- A missing world directory surfaces as a warning chip and disables Clone, but does not auto-remove the catalog entry.
