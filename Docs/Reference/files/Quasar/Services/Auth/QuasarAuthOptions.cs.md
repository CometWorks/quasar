# Quasar/Services/Auth/QuasarAuthOptions.cs

**Module:** Quasar.Services.Auth  **Kind:** class  **Tier:** 2

## Summary
Configuration POCO hierarchy for Quasar's authentication and Steam Workshop features, read from the `Quasar:Auth` configuration section. Each nested options class exposes a `Normalize()` method that sanitises and clamps values to valid ranges.

## Structure
Namespace: `Quasar.Services.Auth`

All types are `sealed`.

**`QuasarAuthOptions`** — root options
- `Enabled` — master auth on/off switch (default `true`)
- `RequireHttpsForPublicAccess` (default `true`)
- `DefaultProvider` — scheme name, default `"Steam"`
- `TrustedNetworkBypass : TrustedNetworkBypassOptions`
- `Steam : SteamAuthOptions`
- `ExternalProviders : ExternalProviderOptions`
- `Workshop : WorkshopOptions`
- `static Create(IConfiguration)` — reads `Quasar:Auth` section, normalises, returns instance

**`TrustedNetworkBypassOptions`**
- `AllowLoopback` (default `true`), `AllowSameSubnet` (default `true`)
- `TrustedProxies : List<string>` — explicit reverse-proxy IP or CIDR list used by forwarded-header handling
- `Roles : List<string>` — roles granted to trusted-network sessions (default `["admin"]`)
- `Normalize()` — deduplicates proxies, filters roles against `QuasarRoles.All`, falls back to `["admin"]`

**`SteamAuthOptions`**
- `Enabled` (default `true`)
- `Normalize()` — no-op placeholder

**`ExternalProviderOptions`**
- `Oidc : OidcProviderOptions`

**`OidcProviderOptions`**
- `Enabled`, `Authority`, `ClientId`, `ClientSecret`
- `Scopes` (default `["openid","profile","email"]`)
- `NameClaim`, `SubjectClaim`, `EmailClaim`, `RoleClaim` — configurable claim names

**`WorkshopOptions`**
- `Enabled` (default `true`), `AppId` (default 244850)
- `PopularLimit`, `SearchLimit` — clamped 1..50
- `RequiredTags` (default `["Mod"]`), `MatchingFileType`, `PopularQueryType`, `SearchQueryType`
- `CacheMaxAgeSeconds` — clamped 30..3600
- `SearchDebounceMilliseconds` — clamped 100..2000

## Dependencies
- [`Quasar/Services/Auth/QuasarAuthConstants.cs`](QuasarAuthConstants.cs.md) — `QuasarAuthSchemes`, `QuasarRoles`, `SteamAuthConstants`
- `Microsoft.Extensions.Configuration` (BCL/ASP.NET)
