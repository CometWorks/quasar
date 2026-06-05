# Quasar/Components/Pages/Security.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page at `/settings/security`, protected by the `CanManageSecurity` policy, for managing runtime RBAC subject-role mappings and reviewing authentication provider settings. Allows adding/removing `SubjectRoleMapping` entries (provider + subject + role) that are persisted via `RbacConfigCatalog`.

## Structure
- **`@page "/settings/security"`**
- **`@attribute [Authorize(Policy = QuasarPolicyNames.CanManageSecurity)]`**
- **`@implements IDisposable`**
- **`[Inject]`**
  - `QuasarAuthOptions AuthOptions` — read-only display of provider defaults (DefaultProvider, Steam enabled, loopback/subnet bypass).
  - `RbacConfigCatalog RbacConfigCatalog`
  - `ISnackbar Snackbar`
  - `IDialogService DialogService`
- **Key UI**
  - Left summary card — displays `AuthOptions` values from `appsettings.json` (read-only).
  - Right RBAC section — add-mapping form: Provider `MudSelect` (Steam / Oidc), Subject `MudTextField` (label changes to "SteamID" when Steam is selected), Role `MudSelect` (all `QuasarRoles.All` entries).
  - `MudTable<SubjectRoleMapping>` — Provider, Subject (monospaced), Roles, Delete button per row.
- **Key methods**
  - `AddMappingAsync` — appends a new `SubjectRoleMapping` and calls `SaveAsync`.
  - `RemoveMappingAsync` — shows `ShowMessageBoxAsync` confirmation then removes the mapping.
  - `SaveAsync` — calls `RbacConfigCatalog.SaveAsync(_config)`, reloads, shows snackbar.
  - `HandleRbacChanged` — live reload when catalog changes externally.

## Dependencies
- `Quasar/Services/RbacConfigCatalog.cs`
- `Quasar/Models/RbacConfig.cs`, `SubjectRoleMapping.cs`
- `Quasar/Auth/QuasarAuthOptions.cs`
- `Quasar/Auth/QuasarPolicyNames.cs`, `QuasarRoles.cs`, `QuasarAuthSchemes.cs`
- MudBlazor — `MudGrid`, `MudPaper`, `MudSelect`, `MudTextField`, `MudTable`, `MudIconButton`, `ISnackbar`, `IDialogService`.

## Notes
- Page is authorization-gated; unauthenticated or insufficiently privileged users are redirected by the ASP.NET Core authorization middleware before reaching this component.
- The OIDC provider name is the constant `"Oidc"` — not derived from a shared constant.
