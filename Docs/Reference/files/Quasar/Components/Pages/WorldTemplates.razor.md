# Quasar/Components/Pages/WorldTemplates.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page at `/world-templates` for managing reusable Space Engineers world templates. Provides a two-tab import card: a Predefined Worlds tab for one-click importing installed Dedicated Server world/scenario templates without entering a name or path, and a Custom World Import tab with name, description, source path, and folder browser controls. The folder browser is seeded from the last imported source folder or the managed Dedicated Server content folders (`Content/CustomWorlds`, `Content/QuickStarts`, `Content/Scenarios`) and shows those locations as shortcut chips. A sortable table lists existing templates with size and missing-world indicators plus Clone and Delete actions. Template world files are copied into managed Quasar storage via `QuasarWorldTemplateCatalog`.

## Structure
- **`@page "/world-templates"`**, **`@implements IDisposable`**
- **`[Inject]`:** `QuasarWorldTemplateCatalog WorldTemplateCatalog`, `ISnackbar Snackbar`, `IDialogService DialogService`, `WorldTemplateImportLocationService ImportLocations`
- **Key UI**
  - Left panel (xl:5) — `MudTabs` import card with separate Predefined Worlds and Custom Import panels; panels are not kept alive when hidden.
  - Predefined Worlds tab — shows discovered installed Space Engineers templates from DS `Content/CustomWorlds`, `Content/QuickStarts`, and `Content/Scenarios`, with search, Refresh, source/category display, and per-row Add buttons. The table uses `installed-world-template-*` classes so the action column stays fixed and the source path truncates inside narrow docked containers.
  - Custom Import tab — `MudTextField` controls for name, description, and source path with a "Browse" folder-picker button, plus Import (shows "Importing…" while `_importing`) and Clear buttons.
  - Right panel (xl:7) — `MudTable<WorldTemplateRow>` with sortable columns Name, Description, Size (MB or a "missing" warning chip), Updated; row actions Clone (disabled when world missing or importing) and Delete.
- **`WorldTemplateRow` (private sealed record)** — `(QuasarWorldTemplate Template, bool WorldExists, long FileSizeMb)`.
- **`Templates` computed property** — maps catalog entries to rows, computing the on-disk world directory size in MB by summing all file lengths (`DirectoryInfo.GetFiles("*", AllDirectories)`).
- **Installed sources:** `_installedTemplates`, `_installedTemplateSearch`, `FilteredInstalledTemplates`, `MatchesInstalledTemplateSearch`.
- **Key methods**
  - `ImportAsync` — validates name and source path are present, calls `WorldTemplateCatalog.ImportAsync(name, description, sourcePath)`, then resets the form.
  - `ImportInstalledTemplateAsync` — imports an `InstalledWorldTemplateSource` using its discovered display name, description, and source path without requiring manual form fields.
  - `CloneAsync` — re-imports using the template's managed world directory as the source, naming it "<name> (Copy)".
  - `OpenFolderPickerAsync` — opens `FolderPickerDialog` seeded with the current source path, the remembered last source folder, or the first existing managed DS content folder; passes DS content shortcut chips and remembers the picked path.
  - `DeleteAsync` — confirms via `ShowMessageBoxAsync`, then `WorldTemplateCatalog.DeleteAsync`.
  - `RefreshInstalledTemplates`, `ResetForm`, `HandleChanged`.
- Subscribes to `WorldTemplateCatalog.Changed` in `OnInitialized`, releases in `Dispose`.

## Dependencies
- [`Quasar/Services/QuasarWorldTemplateCatalog.cs`](../../Services/QuasarWorldTemplateCatalog.cs.md) — import/delete, world directory resolution
- [`Quasar/Services/WorldTemplateImportLocationService.cs`](../../Services/WorldTemplateImportLocationService.cs.md) — source-folder shortcuts, installed template discovery, and last-folder persistence
- `Quasar/Models/QuasarWorldTemplate.cs` — `QuasarWorldTemplate`
- `Quasar/Components/Shared/FolderPickerDialog.razor`
- MudBlazor — `MudGrid`, `MudTabs`, `MudPaper`, `MudTable`, `MudTextField`, `MudButton`, `MudChip`, `MudAlert`, `ISnackbar`, `IDialogService`

## Notes
- Directory size is recomputed inline on every render by walking all files, which can be slow for large templates.
- A missing world directory surfaces as a warning chip and disables Clone, but does not auto-remove the catalog entry.
- The predefined-world source column is intentionally ellipsized so long DS paths cannot push the Add button outside the docked import panel.
