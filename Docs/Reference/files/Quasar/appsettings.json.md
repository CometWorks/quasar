# Quasar/appsettings.json

**Module:** Quasar.Host  **Kind:** JSON config  **Tier:** 3

## Summary
Default application configuration file for the Quasar host. Provides baseline values for the `Quasar` options section (network, managed runtime paths, logging, auth) and ASP.NET Core logging. All keys are overridable via environment-specific `appsettings.{env}.json`, environment variables, or command-line arguments as resolved by `Program.AddDeploymentConfigurationSources`.

## Structure

**`Logging`** — standard ASP.NET Core log-level block; default `Information`, `Microsoft.AspNetCore` at `Warning`.

**`Quasar`** section (maps to `WebServiceOptions` and related):
- `Host`: `"0.0.0.0"` — listen interface
- `Port`: `58631` — listen port
- `Mode`: `"Console"`
- `OpenBrowserOnStart`: `true`
- `AvoidSimultaneousScheduledRestarts`: `true`
- `PreserveManagedServersOnShutdown`: `true`
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
- `TrustedNetworkBypass`: loopback+same-subnet allowed, roles `["admin"]`, empty `TrustedProxies`
- `Steam.Enabled`: `true`
- `ExternalProviders.Oidc`: disabled template with authority/clientId/clientSecret/scopes/claim fields
- `Workshop`: enabled, `AppId` 244850 (SE), popular/search limits 50, required tag `"Mod"`, cache 300 s, debounce 350 ms

**`AllowedHosts`**: `"*"`

## Dependencies
- [`Quasar/Services/WebServiceOptions.cs`](Services/WebServiceOptions.cs.md) (binds `Quasar` root)
- `Quasar/Services/ManagedRuntimeOptions.cs` (binds `Quasar:ManagedRuntime`)
- `Quasar/Services/Auth/QuasarAuthOptions.cs` (binds `Quasar:Auth`)
- [`Quasar/Program.cs`](Program.cs.md) (configuration loading logic)
