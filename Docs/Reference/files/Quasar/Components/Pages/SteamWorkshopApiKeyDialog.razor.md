# Quasar/Components/Pages/SteamWorkshopApiKeyDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Small MudBlazor dialog for entering or updating the Steam Web API key used server-side for Workshop search. The key is treated as a password field and returned to the caller as a trimmed string via `DialogResult.Ok(key)`. The dialog highlights the Steam developer API-key page and explains the platform-specific storage protection Quasar applies after saving.

## Structure
- **No `@page` route** — dialog only.
- **`[CascadingParameter]` `IMudDialogInstance MudDialog`**
- **`[Parameter]`**
  - `string CurrentWebApiKey` — pre-populates the field when editing an existing key.
- **Key UI**
  - Explanatory `MudText` plus an outlined info alert with a prominent bold `MudLink` to `https://steamcommunity.com/dev/apikey`.
  - Platform-specific storage text: Windows/Linux/macOS report the Quasar install directory as the default storage root, all platforms mention the `QUASAR_DATA_DIR` override and Data Protection keyring dependency, and Linux/macOS mention owner-only credentials-file permissions when supported.
  - `MudTextField` with `InputType.Password` and a key adornment icon.
  - Cancel / Save buttons; Save is disabled while the field is blank.
- **`Save`** — trims the key, returns `DialogResult.Ok(key)`.
- **Storage-description helpers** — `StorageProtectionDescription`, `GetPlatformStorageLocation()`, `GetPlatformFileProtectionDescription()`.

## Dependencies
- MudBlazor — `MudDialog`, `MudAlert`, `MudStack`, `MudTextField`, `MudLink`, `MudButton`.
- `MagnetarPaths.GetQuasarWorkshopOptionsPath()` — file name shown in the storage text.

## Notes
- Key storage and persistence is handled by the caller; this component only collects and validates non-empty input.
