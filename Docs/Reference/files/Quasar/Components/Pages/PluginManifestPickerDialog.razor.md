# Quasar/Components/Pages/PluginManifestPickerDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Modal MudBlazor dialog used by the config-template editor to browse the server filesystem and select a plugin XML manifest. It supports path entry, folder navigation, breadcrumbs, shortcut chips, hidden-entry toggling, automatic selection when a folder has exactly one XML file, and returns the selected manifest path via `DialogResult.Ok`.

## Structure
- **Injected services:** `FileBrowserService`
- **Parameters:** `InitialPath` seeds the first folder; `DialogTitle` customises the title text.
- **State:** `_currentPath`, `_selectedXmlPath`, `_entries`, `_xmlFiles`, `_breadcrumbs`, `_shortcuts`, `_showHidden`, `_error`.
- **UI sections:** title row with close button, path input with refresh adornment, Go/Up controls, hidden-entry checkbox, shortcut chips, breadcrumb buttons, warning/info alerts, scrollable directory/XML file list, Cancel and Use this manifest actions.
- **Key methods:** `OnInitialized()` loads shortcuts and navigates to the initial path; `NavigateTo()` resolves a path and refreshes folder/XML/breadcrumb state; `SelectXml()` marks a manifest; `HandlePathKeyDownAsync()` navigates on Enter; `HandleShowHiddenChanged()` refreshes with hidden entries; `UseSelected()` closes the dialog with the selected XML path.

## Dependencies
- [`Quasar/Services/FileBrowserService.cs`](../../Services/FileBrowserService.cs.md)
- [`Quasar/Components/Pages/Configs.razor`](Configs.razor.md) (opens the dialog when adding a local plugin dev folder)
- MudBlazor (`MudDialog`, `MudTextField`, `MudButton`, `MudIconButton`, `MudCheckBox`, `MudChip`, `MudList`)

## Notes
- Navigation errors are displayed inside the dialog rather than thrown to the caller.
- The dialog lists directories separately from XML manifest files, so selecting a folder navigates while selecting an XML file enables the confirm action.
