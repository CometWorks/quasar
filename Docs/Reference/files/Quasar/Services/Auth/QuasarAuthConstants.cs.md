# Quasar/Services/Auth/QuasarAuthConstants.cs

**Module:** Quasar.Services.Auth  **Kind:** class  **Tier:** 2

## Summary
Defines all authentication-related string constants, role names, policy names, and a `ClaimsPrincipal` extension method used throughout the auth pipeline. Acts as the single source of truth for scheme identifiers, custom claim type keys, and the named authorization policies that guard Quasar features.

## Structure
Namespace: `Quasar.Services.Auth`

Five static classes, all non-inheritable:

- **`QuasarAuthSchemes`** — auth scheme name constants
  - `Cookie = "QuasarCookie"` — cookie-based session scheme
  - `TrustedNetwork = "QuasarTrustedNetwork"` — loopback/subnet bypass scheme
  - `Steam = "Steam"` — Steam OpenID scheme

- **`QuasarClaimTypes`** — custom claim type URI constants
  - `Provider` — which auth scheme issued the principal
  - `SteamId` — Steam 64-bit ID string
  - `SteamProfileUrl` — Steam profile URL

- **`QuasarRoles`** — role name constants
  - `Viewer`, `Editor`, `Admin`
  - `All` — `IReadOnlySet<string>` of all valid roles (case-insensitive)

- **`QuasarPolicyNames`** — named ASP.NET authorization policy constants
  - `CanView`, `CanEditConfigs`, `CanEditServers`, `CanControlServers`, `CanManageDiscord`, `CanManageAppearance`, `CanManageSecurity`, `CanShutdownQuasar`

- **`SteamAuthConstants`** — Steam OpenID endpoint and Space Engineers app constants
  - `OpenIdEndpoint`, `ClaimedIdPrefix`, `ClaimedIdPrefixHttp`, `SpaceEngineersAppId = 244850`

- **`ClaimsPrincipalExtensions`** — extension on `ClaimsPrincipal`
  - `GetQuasarDisplayName()` — returns "Trusted network" for trusted-network principals, "Steam {steamId}" for Steam logins, or `Identity.Name` fallback

## Dependencies
- `System.Security.Claims` (BCL)

## Notes
The `All` set uses `StringComparer.OrdinalIgnoreCase`, so role validation elsewhere is case-insensitive. The display-name logic distinguishes trusted-network sessions from Steam sessions using the `Provider` claim.
