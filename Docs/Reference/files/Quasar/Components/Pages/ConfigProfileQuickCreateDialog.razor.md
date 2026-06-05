# Quasar/Components/Pages/ConfigProfileQuickCreateDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Modal dialog for quickly creating a new `QuasarConfigProfile` (config template) from the Home dashboard setup wizard. Validates that a name is provided, persists the new profile via `QuasarConfigProfileCatalog`, and returns the created `QuasarConfigProfile` to the caller on success.

## Structure
- **No route** (dialog component only)
- **Cascading parameter:** `IMudDialogInstance MudDialog`
- **Injected services:** `QuasarConfigProfileCatalog` (DI), `ISnackbar` (DI)
- **UI:**
  - `MudForm` bound to `_formIsValid` for validation gating.
  - `MudTextField` for template name (Required, AutoFocus, Immediate).
  - `MudTextField` for description (optional, 2 lines).
  - Cancel and "Create Template" buttons; button is disabled while `_saving`.
- **Key state:** `_name`, `_description`, `_saving` (bool), `_formIsValid` (bool).
- **`CreateAsync()`** — validates the form, constructs a `QuasarConfigProfile`, calls `ConfigProfiles.UpsertAsync`, closes with `DialogResult.Ok(template)`. Catches and snacks exceptions.
- Returns `QuasarConfigProfile` as the dialog result data.

## Dependencies
- [`Quasar/Services/QuasarConfigProfileCatalog.cs`](../../Services/QuasarConfigProfileCatalog.cs.md)
- [`Quasar/Models/QuasarConfigProfile.cs`](../../Models/QuasarConfigProfile.cs.md)
- MudBlazor (`MudDialog`, `MudForm`, `MudTextField`, `MudButton`, `ISnackbar`)
