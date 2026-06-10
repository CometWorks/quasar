# Quasar/Components/Pages/Configs.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
The `/configs` page: a full editor for reusable Magnetar config templates (`QuasarConfigProfile`) that are applied to assigned dedicated servers at startup. A sidebar lists/creates/clones/deletes templates; the main column edits World settings, Plugins, Mods, and Developer dev-folders across tabbed and collapsible panels. QoL features include searchable/jump-to world options, a refreshable plugin catalog, Steam Workshop search, world-template mod merge, dead Workshop mod cleanup, unsaved-change guarding, and integration with the plugin-manifest picker dialog for registering local dev folders. (This page has no charts; the analytics charting work lives in `Analytics.razor`.)

## Structure
- `@page "/configs"`, `@implements IDisposable`.
- **`[Inject]`ed services:** `QuasarConfigProfileCatalog ConfigProfiles`, `QuasarDevFolderCatalog DevFolderCatalog`, `QuasarPluginCatalogService PluginCatalog`, `QuasarWorkshopModResolver WorkshopMods`, `SteamWorkshopCredentialsCatalog WorkshopCredentials`, `DedicatedServerCatalog ServerCatalog`, `ISnackbar Snackbar`, `IJSRuntime JS`, `IDialogService DialogService`.
- **`[Parameter]`s:** `InitialProfileId`; `RequestedProfileId` (`[SupplyParameterFromQuery(Name="profileId")]`) — selects the initial template.
- **Sidebar:** new-template name field (Enter to create), template tiles (subtitle summary "N plugins, N mods, N servers"), clone/delete icon buttons; selection guarded by `ConfirmPendingChangesBeforeSwitchAsync`.
- **Header paper:** name/description fields, count chips (Plugins/Mods/Assigned Servers, "Catalog stale" warning), Save / Reset buttons.
- **World tab:** search field + "Jump to First Match" + match-count chip; categories as `MudExpansionPanel`s (single-expansion) driven by `QuasarConfigMetadata.Categories`/`Options`, including a dedicated **Survival** section for game mode, production, respawn, oxygen/radiation, hunger, and progression settings. Each option renders by `QuasarConfigOptionKind` (Boolean/Integer/Decimal/SelectInteger/SelectText/LongText/Text). A synthetic **Access** panel edits whitelist Group ID, Admin/Reserved/Banned ID lists (numeric-filtered, parsed via `ParseUnsignedLongList`/`SplitListTokens`).
- **Plugins tab:** "Plugins to load" table (filter, GitHub link, remove); "Plugin catalog" panel (search, Refresh Catalog, selection checkboxes, hidden/local-dev/auto-managed handling, full-Tooltip column with a shortened-Description fallback + `PluginCatalogDescriptionDialog` showing the full Description); "Advanced/manual plugin setup" (add custom plugin ID).
- **Mods tab:** "Mod list" table (+ "Merge from World Template" → `MergeWorldTemplateModsDialog`, "Remove Dead Mods" → `WorkshopMods.CheckAvailabilityAsync` and a removed-mod summary); "Steam Workshop" panel (search/popular, API-key chip + `SteamWorkshopApiKeyDialog`, results table with thumbnails); "Advanced/manual mod setup" (resolve URLs/IDs/collections via `WorkshopMods.ResolveAsync`, manual add).
- **Developer panel:** dev-folder table (debug switch, remove) and "Add dev folder..." → `PluginManifestPickerDialog`; the picked XML manifest is validated/read via `PluginManifestReader` and registered in `DevFolderCatalog`.
- **State helpers:** profile snapshot/`HasPendingChanges` (JSON diff ignoring `UpdatedAtUtc`), category/panel expansion tracking, search matching (`MatchesSearchTerms`), option get/set via reflection through `QuasarConfigMetadata`, `JumpToFirstOptionAsync` uses JS interop `quasarConfigs.focusElement`.

## Dependencies
- `Quasar/Services/QuasarConfigProfileCatalog.cs`, `Quasar/Services/QuasarDevFolderCatalog.cs`, `Quasar/Services/QuasarPluginCatalogService.cs`, `Quasar/Services/QuasarWorkshopModResolver.cs`, `Quasar/Services/SteamWorkshopCredentialsCatalog.cs`, `Quasar/Services/DedicatedServerCatalog.cs`
- Models: `QuasarConfigProfile`, `QuasarConfigMetadata` / `QuasarConfigOptionDefinition` / `QuasarConfigOptionCategory` / `QuasarConfigOptionKind` / `QuasarConfigOptionScope`, `QuasarPluginSelection`, `QuasarPluginCatalogEntry`, `QuasarModSelection`, `QuasarDevFolderSelection`, `QuasarWorkshopSearchResult(Set)`, `QuasarNetworkType`, `DedicatedServerDefinition`, `SteamWorkshopCredentials`, `PluginManifestReader`
- Dialogs: `Quasar/Components/Pages/PluginManifestPickerDialog.razor`, `MergeWorldTemplateModsDialog.razor`, `PluginCatalogDescriptionDialog.razor`, `SteamWorkshopApiKeyDialog.razor`, `ConfigProfilePendingChangesDialog.razor`
- [`Quasar/Components/Pages/Configs.razor.css`](Configs.razor.css.md) (scoped styles)
- External: **MudBlazor**, `System.Text.Json`, `Microsoft.JSInterop`.

## Notes
- Switching/resetting templates is guarded against unsaved edits via a snapshot JSON comparison and the pending-changes dialog.
- Plugin selection respects MagnetarHub rules: auto-managed plugins (`IsManualSelectionAllowed`) and hidden/local-dev entries cannot be manually selected.
- The plugin catalog auto-refreshes on first render and when its panel is expanded; popular workshop mods auto-load if an API key is configured.
- "Remove Dead Mods" checks selected Workshop IDs through Steam published-file details, removes unavailable entries from the in-memory profile editor, and requires Save to persist the cleaned list.
- ID list inputs are filtered to digits/separators and validated; invalid tokens are reported via snackbar.
