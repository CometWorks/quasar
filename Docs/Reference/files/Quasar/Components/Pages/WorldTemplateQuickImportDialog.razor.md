# Quasar/Components/Pages/WorldTemplateQuickImportDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
MudBlazor dialog for adding a Space Engineers world template without leaving the server editor. The first step is a two-tab card: Predefined Worlds lists installed Dedicated Server world/scenario templates for one-click import, while Custom Import validates a name and absolute source path and opens a `FolderPickerDialog` for path browsing. Folder browsing shows shortcut chips for managed Dedicated Server content folders (`Content/CustomWorlds`, `Content/QuickStarts`, `Content/Scenarios`) and remembers the last selected source folder. After a source is chosen, the dialog reads source-world mods from `Sandbox_config.sbc`, lets the user create a config profile, merge into an existing profile, or ignore those mods, and returns the imported template plus an optional config-profile id.

## Structure
- **No `@page` route** — dialog only; launched from `ServerEditorDialog`.
- **`[Inject]`**
  - `QuasarWorldTemplateCatalog WorldTemplates`
  - `QuasarConfigProfileCatalog ConfigProfiles`
  - `ISnackbar Snackbar`
  - `IDialogService DialogService`
  - `WorldTemplateImportLocationService ImportLocations`
- **`[CascadingParameter]` `IMudDialogInstance MudDialog`**
- **`[Parameter]` `InitialConfigProfileId`** — preselects the currently selected server-editor config profile for the "existing profile" mod path.
- **Key UI**
  - Step 1 `MudTabs` card with separate Predefined Worlds and Custom Import panels; hidden panels are not kept alive.
  - Predefined Worlds tab lists discovered installed DS templates with search, Refresh, source/category display, and per-row Add buttons. The table uses `installed-world-template-*` classes so the left Add column remains visible while long source paths truncate within the dialog width.
  - Custom Import tab contains the original `MudForm` and `@bind-IsValid` details form: required Name, optional multi-line Description, and source world path text field + "Browse" button that opens `FolderPickerDialog`.
  - Step 2 mod handling view when source mods are found, with radio options for creating a profile, importing into an existing profile, or doing nothing. The info alert explains that Quasar writes the selected profile's session settings and mods into the active world's `Sandbox_config.sbc` on server start.
  - Mod preview table listing display name and Workshop ID.
  - Back / Cancel / primary action buttons; the primary Continue button is only shown for Custom Import or the mod-handling step, while Predefined Worlds uses row-level Add buttons. Primary text shows "Importing..." while `_importing` is true.
- **`OpenFolderPickerAsync`** — opens `FolderPickerDialog` with the current path, remembered last path, or first existing DS content shortcut; applies and remembers the selected path on non-cancelled result.
- **`ImportPredefinedTemplateAsync`** — copies display name, description, and source path from an `InstalledWorldTemplateSource`, then follows the same mod-detection/import flow as Custom Import.
- **`ContinueAsync` / `ContinueFromDetailsAsync`** — validates custom details, reads source mods via `WorldSandboxConfigEditor.ReadMods`, advances to mod handling when mods exist, or imports immediately when no mods are present.
- **`ImportAsync`** — validates selected mod action, imports the world template, applies the profile action, and closes with `Ok(WorldTemplateQuickImportResult)`.
- **`ApplyModActionAsync`** — merges mods into an existing profile or creates a new profile preloaded with mods; the create path opens `ConfigsPageDialog` full-screen on the new profile so it can be edited before returning to the server editor.
- **Installed sources:** `_installedTemplates`, `_installedTemplateSearch`, `FilteredInstalledTemplates`, `RefreshInstalledTemplates`, `MatchesInstalledTemplateSearch`.
- **`MergeMods`** — appends only missing Workshop IDs, preserves names, and sorts profile mods.

## Dependencies
- [`Quasar/Services/QuasarWorldTemplateCatalog.cs`](../../Services/QuasarWorldTemplateCatalog.cs.md)
- [`Quasar/Services/QuasarConfigProfileCatalog.cs`](../../Services/QuasarConfigProfileCatalog.cs.md)
- [`Quasar/Services/WorldSandboxConfigEditor.cs`](../../Services/WorldSandboxConfigEditor.cs.md)
- [`Quasar/Services/WorldTemplateImportLocationService.cs`](../../Services/WorldTemplateImportLocationService.cs.md)
- `Quasar/Components/Shared/FolderPickerDialog.razor`
- [`Quasar/Components/Pages/ConfigsPageDialog.razor`](ConfigsPageDialog.razor.md)
- [`Quasar/Models/QuasarWorldTemplate.cs`](../../Models/QuasarWorldTemplate.cs.md)
- [`Quasar/Models/QuasarConfigProfile.cs`](../../Models/QuasarConfigProfile.cs.md)
- MudBlazor — `MudDialog`, `MudTabs`, `MudForm`, `MudTextField`, `MudSelect`, `MudRadioGroup`, `MudTable`, `MudButton`, `MudChip`, `MudAlert`, `ISnackbar`, `IDialogService`.
