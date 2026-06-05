# Quasar/Components/Pages/Configs.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page (`/configs`) and the primary config-template editor. Provides a sidebar list of `QuasarConfigProfile` templates and a main editor pane with three tabs — World (categorised world-option expansion panels with live search and jump-to), Plugins (selected plugins table + MagnetarHub catalog table + manual input), and Mods (selected mods table + Steam Workshop search + manual/bulk input). Also contains a Developer section for registering local plugin dev folders. Supports pending-change detection with an interstitial dialog before switching templates.

## Structure
- **Route:** `@page "/configs"`
- **Implements:** `IDisposable`
- **Injected services:** `QuasarConfigProfileCatalog`, `QuasarDevFolderCatalog`, `QuasarPluginCatalogService`, `QuasarWorkshopModResolver`, `SteamWorkshopCredentialsCatalog`, `DedicatedServerCatalog`, `ISnackbar`, `IJSRuntime` (JS), `IDialogService`
- **Parameters:**
  - `InitialProfileId` (string?) — profile to select on mount (passed from `ConfigsPageDialog`).
  - `RequestedProfileId` (string?) — from query string `?profileId=`.
- **Key UI sections:**
  - Sidebar: create-template text field + button, scrollable list of template tiles with clone/delete icon buttons, selection highlight.
  - World tab: search field with "Jump to First Match" button + match count chip; `MudExpansionPanels` per category; special-cased Access section (group ID, admin IDs, reserved, banned); option cards render `MudCheckBox`, `MudNumericField`, `MudSelect`, or `MudTextField` depending on `QuasarConfigOptionKind`.
  - Plugins tab: expansion panels for "Plugins to load" table, "Plugin catalog" table (from MagnetarHub), and "Advanced/manual" custom plugin-ID input.
  - Mods tab: expansion panels for "Mod list" table, "Steam Workshop" search/results table with thumbnail images, and "Advanced/manual" bulk URL/ID input + single add form.
  - Developer section: dev-folder table with debug toggle; inline editor for name, manifest file, folder path (with Browse button → `FolderPickerDialog`).
  - Summary header chips (plugin count, mod count, assigned server count).
  - Save/Reset buttons per template.
- **JS interop:** `JS.InvokeVoidAsync("quasarConfigs.focusElement", anchorId)` to scroll-focus matched world options.
- **Pending-change detection:** JSON-serialised snapshot (`CreateProfileSnapshot`) compared on template switch; if different, shows `ConfigProfilePendingChangesDialog` (Save/Discard/Cancel).
- **Key methods:** `SelectProfileFromListAsync`, `ConfirmPendingChangesBeforeSwitchAsync`, `SaveTemplateAsync`, `CloneProfileAsync`, `DeleteProfileAsync`, `RefreshPluginCatalogAsync`, `SearchWorkshopModsAsync`, `LoadPopularWorkshopModsAsync`, `ResolveWorkshopModsAsync`, `OpenWorkshopApiKeyDialogAsync` (`SteamWorkshopApiKeyDialog`), `OpenDevFolderPickerAsync` (`FolderPickerDialog`), `JumpToFirstOptionAsync`.
- **Event subscriptions:** `QuasarConfigProfileCatalog.Changed`, `QuasarDevFolderCatalog.Changed`, `DedicatedServerCatalog.Changed`, `SteamWorkshopCredentialsCatalog.Changed`.

## Dependencies
- `Quasar/Services/QuasarConfigProfileCatalog.cs`
- `Quasar/Services/QuasarDevFolderCatalog.cs`
- `Quasar/Services/QuasarPluginCatalogService.cs`
- `Quasar/Services/QuasarWorkshopModResolver.cs`
- `Quasar/Services/SteamWorkshopCredentialsCatalog.cs`
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md)
- `Quasar/Models/QuasarConfigProfile.cs`
- `Quasar/Models/QuasarConfigMetadata.cs` (option definitions and categories)
- `Quasar/Components/Pages/ConfigProfilePendingChangesDialog.razor`
- `Quasar/Components/Pages/FolderPickerDialog.razor`
- MudBlazor (`MudExpansionPanels`, `MudTable`, `MudTabs`, `MudCheckBox`, `MudNumericField`, `MudSelect`, `MudTextField`, `IDialogService`)

## Notes
- Pending-change detection compares JSON snapshots after zeroing `UpdatedAtUtc`, so timestamps do not cause false positives.
- The Workshop search requires a Steam Web API key stored in `SteamWorkshopCredentialsCatalog`; if absent, search/popular buttons are disabled.
- `QuasarPluginCatalogService.IsManualSelectionAllowed` is checked before adding a plugin; automatic and hidden plugins display informational snackbars.
- Dev folder entries are global (shared across all config profiles); enabling them per-profile is done by selecting the dev-folder plugin in the Plugins tab.
