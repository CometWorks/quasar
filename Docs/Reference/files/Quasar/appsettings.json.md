# Quasar/appsettings.json

**Module:** Quasar.Host  **Kind:** JSON config  **Tier:** 3

## Summary
Default application configuration for the Quasar host. Provides baseline values for the `Quasar` options section (network, analytics retention, agent reconnect timing, managed runtime paths, logging, auth) plus ASP.NET Core logging. All keys are overridable via environment-specific `appsettings.{env}.json`, environment variables, or command-line arguments as resolved by the deployment configuration sources in `Program`.

## Structure

**`Logging`** — standard ASP.NET Core log-level block; `Default` = `Information`, `Microsoft.AspNetCore` = `Warning`.

**`Quasar`** root section (maps to `WebServiceOptions` and related):
- `Host`: `"0.0.0.0"`, `Port`: `58631`, `Mode`: `"Console"`
- `OpenBrowserOnStart`: `true`
- `AvoidSimultaneousScheduledRestarts`: `true`
- `PreserveManagedServersOnShutdown`: `true`
- `Analytics.RetentionDays`: `30` — retention window for analytics data (only the `Analytics` subsection is present; there is no `AnalyticsStore` section in this file)
- `AgentOfflineShutdownSeconds`: `3600`
- `AgentReconnectIntervalSeconds`: `10`, `AgentReconnectJitterSeconds`: `3`

**`Quasar.ManagedRuntime`** (maps to `ManagedRuntimeOptions`):
- `MagnetarArchiveUrl`, `MagnetarInstallDirectory`
- `SteamCmdArchiveUrl`, `SteamCmdInstallDirectory`
- `DedicatedServerInstallDirectory`, `DedicatedServer64OverridePath`, `SteamCmdPath`
- `PreferManagedDedicatedServerInstall`: `true`

**`Quasar.Logging`**:
- `Directory`: empty (defaults to app data), `Format`: `"text"`, `MinimumLevel`: `"Info"`

**`Quasar.Auth`** (maps to `QuasarAuthOptions`):
- `Enabled`: `true`, `RequireHttpsForPublicAccess`: `true`, `DefaultProvider`: `"Steam"`
- `TrustedNetworkBypass`: `AllowLoopback`/`AllowSameSubnet` true, empty `TrustedProxies`, roles `["admin"]`
- `Steam.Enabled`: `true`
- `ExternalProviders.Oidc`: disabled template (`Authority`, `ClientId`, `ClientSecret`, `Scopes`, `NameClaim`/`SubjectClaim`/`EmailClaim`/`RoleClaim`)
- `Workshop`: enabled, `AppId` 244850 (SE), popular/search limits 50, required tag `"Mod"`, `MatchingFileType` `Items`, query types `RankedByTotalUniqueSubscriptions`/`RankedByTextSearch`, cache 300 s, search debounce 350 ms

**`AllowedHosts`**: `"*"`

## Dependencies
- [`Quasar/Program.cs`](Program.cs.md) (configuration loading logic)
- Options types bound from the `Quasar` section (web service, managed runtime, logging, auth options)
