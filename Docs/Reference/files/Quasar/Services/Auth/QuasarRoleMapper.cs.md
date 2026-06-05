# Quasar/Services/Auth/QuasarRoleMapper.cs

**Module:** Quasar.Services.Auth  **Kind:** class  **Tier:** 2

## Summary
Translates authenticated identities into `ClaimsPrincipal` objects with Quasar roles attached. Serves as the bridge between the RBAC config catalog and ASP.NET's claims-based identity model, producing fully-formed principals for both Steam and trusted-network sessions.

## Structure
Namespace: `Quasar.Services.Auth`

`sealed class QuasarRoleMapper` — no base class, no interface.

Constructor: `(QuasarAuthOptions options, RbacConfigCatalog rbacConfigCatalog)`

Public members:
- `IsSteamIdAllowed(string steamId) : bool` — returns `true` if the Steam ID is non-empty (currently no further restriction)
- `GetSteamRoles(string steamId) : IReadOnlyList<string>` — looks up roles for the Steam provider/subject pair in the RBAC catalog, returns sorted distinct list
- `CreateSteamPrincipal(string steamId) : ClaimsPrincipal` — builds a `ClaimsIdentity` authenticated as `"Steam"` with `NameIdentifier`, `Name`, `Provider`, `SteamId`, `SteamProfileUrl`, and all role claims
- `CreateTrustedNetworkPrincipal() : ClaimsPrincipal` — builds a `ClaimsIdentity` authenticated as `"QuasarTrustedNetwork"` with roles taken from `TrustedNetworkBypassOptions.Roles`

## Dependencies
- [`Quasar/Services/Auth/QuasarAuthConstants.cs`](QuasarAuthConstants.cs.md) — scheme/claim-type/role constants
- [`Quasar/Services/Auth/QuasarAuthOptions.cs`](QuasarAuthOptions.cs.md) — `QuasarAuthOptions`, `TrustedNetworkBypassOptions`
- [`Quasar/Services/Auth/RbacConfigCatalog.cs`](RbacConfigCatalog.cs.md) — `RbacConfigCatalog.GetSubjectRoles`
- `System.Security.Claims` (BCL)
