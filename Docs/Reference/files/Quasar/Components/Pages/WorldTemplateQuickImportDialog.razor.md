# Quasar/Components/Pages/WorldTemplateQuickImportDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
MudBlazor dialog for importing a Space Engineers save folder as a world template without leaving the server editor. Validates a name and absolute source path, opens a `FolderPickerDialog` for path browsing, reads source-world mods from `Sandbox_config.sbc`, lets the user create a config profile, merge into an existing profile, or ignore those mods, and returns the imported template plus an optional config-profile id.

## Structure
- **No `@page` route** — dialog only; launched from `ServerEditorDialog`.
- **`[Inject]`**
  - `QuasarWorldTemplateCatalog WorldTemplates`
  - `QuasarConfigProfileCatalog ConfigProfiles`
  - `ISnackbar Snackbar`
  - `IDialogService DialogService`
- **`[CascadingParameter]` `IMudDialogInstance MudDialog`**
- **`[Parameter]` `InitialConfigProfileId`** — preselects the currently selected server-editor config profile for the "existing profile" mod path.
- **Key UI**
  - Step 1 details form with `MudForm` and `@bind-IsValid`.
  - Name field (required, auto-focus), Description field (multi-line, optional).
  - Source world path text field + "Browse" button that opens `FolderPickerDialog`.
  - Step 2 mod handling view when source mods are found, with radio options for creating a profile, importing into an existing profile, or doing nothing. The info alert explains that Quasar writes the selected profile's session settings and mods into the active world's `Sandbox_config.sbc` on server start.
  - Mod preview table listing display name and Workshop ID.
  - Back / Cancel / primary action buttons; primary button shows "Importing..." while `_importing` is true.
- **`OpenFolderPickerAsync`** — opens `FolderPickerDialog` with `InitialPath = _sourcePath`, applies selected path on non-cancelled result.
- **`ContinueAsync`** — validates details, reads source mods via `WorldSandboxConfigEditor.ReadMods`, advances to mod handling when mods exist, or imports immediately when no mods are present.
- **`ImportAsync`** — validates selected mod action, imports the world template, applies the profile action, and closes with `Ok(WorldTemplateQuickImportResult)`.
- **`ApplyModActionAsync`** — merges mods into an existing profile or creates a new profile preloaded with mods; the create path opens `ConfigsPageDialog` full-screen on the new profile so it can be edited before returning to the server editor.
- **`MergeMods`** — appends only missing Workshop IDs, preserves names, and sorts profile mods.

## Dependencies
- [`Quasar/Services/QuasarWorldTemplateCatalog.cs`](../../Services/QuasarWorldTemplateCatalog.cs.md)
- [`Quasar/Services/QuasarConfigProfileCatalog.cs`](../../Services/QuasarConfigProfileCatalog.cs.md)
- [`Quasar/Services/WorldSandboxConfigEditor.cs`](../../Services/WorldSandboxConfigEditor.cs.md)
- `Quasar/Components/Shared/FolderPickerDialog.razor`
- [`Quasar/Components/Pages/ConfigsPageDialog.razor`](ConfigsPageDialog.razor.md)
- [`Quasar/Models/QuasarWorldTemplate.cs`](../../Models/QuasarWorldTemplate.cs.md)
- [`Quasar/Models/QuasarConfigProfile.cs`](../../Models/QuasarConfigProfile.cs.md)
- MudBlazor — `MudDialog`, `MudForm`, `MudTextField`, `MudSelect`, `MudRadioGroup`, `MudTable`, `MudButton`, `ISnackbar`, `IDialogService`.
