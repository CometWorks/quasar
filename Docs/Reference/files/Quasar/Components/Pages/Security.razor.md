# Quasar/Components/Pages/Security.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page at `/settings/security`, protected by the `CanManageSecurity` policy, for managing trusted-network/reverse-proxy auth settings, Magnetar data-handling consent, and runtime RBAC subject-role mappings. It persists trusted-network settings into data-directory `appsettings.json` through `QuasarAuthSettingsService`, stores the consent YES/NO decision through `DataHandlingConsentCatalog`, provides a public reverse-proxy preset and checklist, and allows adding/removing `SubjectRoleMapping` entries through `RbacConfigCatalog`.

## Structure
- **`@page "/settings/security"`**
- **`@attribute [Authorize(Policy = QuasarPolicyNames.CanManageSecurity)]`**
- **`@implements IDisposable`**
- **`[Inject]`**
  - `QuasarAuthOptions AuthOptions` — read-only display of provider defaults (DefaultProvider, Steam enabled, loopback/subnet bypass, trusted proxy list).
  - `QuasarAuthSettingsService AuthSettingsService` — trusted-network settings load/save and appsettings path.
  - `DataHandlingConsentCatalog DataHandlingConsent` — stored Magnetar consent decision and file path.
  - `RbacConfigCatalog RbacConfigCatalog`
  - `ISnackbar Snackbar`
  - `IDialogService DialogService`
- **Key UI**
  - Left summary card — displays provider and trusted-network state, including a copyable data-directory `appsettings.json` path and a trusted-proxy summary that shows `Loopback only` when no explicit proxy IP/CIDR entries are configured.
  - Trusted Network and Reverse Proxy panel — editable loopback/same-subnet checkboxes, multiline proxy IP/CIDR text field, save button, public reverse-proxy preset button, restart-needed hint for proxy-list edits, and a step-by-step internet-exposure checklist.
  - Data Handling Consent section — status chip (`YES`, `NO`, or `No choice stored`), copyable `data-handling-consent.json` path, YES/NO buttons that fill only when selected, and an info alert noting changes apply on next server restart.
  - RBAC section — add-mapping form: Provider `MudSelect` (Steam / Oidc), Subject `MudTextField` (label changes to "SteamID" when Steam is selected), Role `MudSelect` (all `QuasarRoles.All` entries).
  - `MudTable<SubjectRoleMapping>` — Delete action, Provider, Roles, and monospaced Subject.
- **Key methods**
  - `ReloadTrustedNetworkSettings` — clones trusted-network settings from `QuasarAuthSettingsService` and formats the proxy list.
  - `ApplyPublicReverseProxyPreset` — sets loopback on and same-subnet off in the editor, pending save.
  - `SaveTrustedNetworkSettingsAsync` — parses/validates proxy text, persists trusted-network settings, reloads editor state, and reports validation/write errors.
  - `SaveDataHandlingConsentAsync` — persists the Magnetar consent YES/NO choice and reports success/failure.
  - `AddMappingAsync` — appends a new `SubjectRoleMapping` and calls `SaveAsync`.
  - `RemoveMappingAsync` — shows `ShowMessageBoxAsync` confirmation then removes the mapping.
  - `SaveAsync` — calls `RbacConfigCatalog.SaveAsync(_config)`, reloads, shows snackbar.
  - `HandleRbacChanged` / `HandleDataHandlingConsentChanged` — live reload when backing catalogs change externally.

## Dependencies
- [`Quasar/Services/Auth/QuasarAuthSettingsService.cs`](../../Services/Auth/QuasarAuthSettingsService.cs.md)
- [`Quasar/Services/WebServiceOptions.cs`](../../Services/WebServiceOptions.cs.md) — `DataHandlingConsentCatalog`
- `Quasar/Services/RbacConfigCatalog.cs`
- `Quasar/Models/RbacConfig.cs`, `SubjectRoleMapping.cs`
- `Quasar/Auth/QuasarAuthOptions.cs`
- `Quasar/Auth/QuasarPolicyNames.cs`, `QuasarRoles.cs`, `QuasarAuthSchemes.cs`
- MudBlazor — `MudGrid`, `MudPaper`, `MudSelect`, `MudTextField`, `MudTable`, `MudIconButton`, `MudExpansionPanels`, `ISnackbar`, `IDialogService`.

## Notes
- Page is authorization-gated; unauthenticated or insufficiently privileged users are redirected by the ASP.NET Core authorization middleware before reaching this component.
- The OIDC provider name is the constant `"Oidc"` — not derived from a shared constant.
- Proxy-list edits are persisted immediately but require a Quasar restart before forwarded-header middleware trusts the new IP/CIDR entries; loopback and same-subnet toggles apply immediately via mutated in-memory auth options.
