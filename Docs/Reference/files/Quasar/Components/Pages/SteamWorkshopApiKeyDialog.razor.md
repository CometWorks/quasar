# Quasar/Components/Pages/SteamWorkshopApiKeyDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Small MudBlazor dialog for entering or updating the Steam Web API key used server-side for Workshop search. The key is treated as a password field and returned to the caller as a trimmed string via `DialogResult.Ok(key)`; it is never exposed to browser JavaScript.

## Structure
- **No `@page` route** — dialog only.
- **`[CascadingParameter]` `IMudDialogInstance MudDialog`**
- **`[Parameter]`**
  - `string CurrentWebApiKey` — pre-populates the field when editing an existing key.
- **Key UI**
  - Explanatory `MudText` + external `MudLink` to `https://steamcommunity.com/dev/apikey`.
  - `MudTextField` with `InputType.Password` and a key adornment icon.
  - Cancel / Save buttons; Save is disabled while the field is blank.
- **`Save`** — trims the key, returns `DialogResult.Ok(key)`.

## Dependencies
- MudBlazor — `MudDialog`, `MudTextField`, `MudLink`, `MudButton`.

## Notes
- Key storage and persistence is handled by the caller; this component only collects and validates non-empty input.
