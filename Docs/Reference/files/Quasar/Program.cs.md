# Quasar/Program.cs

**Module:** Quasar.Host  **Kind:** class  **Tier:** 1

## Summary
The ASP.NET Core / Blazor Server entry point for the Quasar supervisor host. `Program.Main` builds the `WebApplication`, registers every DI service, configures authentication and authorization, wires the middleware pipeline, maps HTTP/WebSocket endpoints, and runs the app. It is the system wiring hub — essentially every service in the process is registered here.

## Structure
Namespace `Quasar`; `public class Program` with `static void Main(string[] args)`.

### Configuration loading
`AddDeploymentConfigurationSources` probes `AppContext.BaseDirectory`, `Directory.GetCurrentDirectory()`, a `WebService/` subdir, the Quasar data directory (`MagnetarPaths.GetQuasarDirectory()`), and up to 8 ancestor directories (incl. ancestor `Quasar/`) for `appsettings.json` / `appsettings.{env}.json`, then adds env vars and command-line args. The data-directory source lets operator UI settings override packaged defaults without editing install files. Up-front strongly-typed options: `WebServiceOptions`, `ManagedRuntimeOptions`, `QuasarUpdateOptions`, `QuasarAuthOptions`, `AnalyticsStoreOptions`. `QuasarLoggingConfigurator.Configure` sets up NLog. Kestrel binds `{host}:{port}` unless `ASPNETCORE_URLS` is set; wildcard hosts (`0.0.0.0`/`[::]`/`*`/`+`) use `ListenAnyIP`.

### DI service registrations (high level)
- Blazor: `AddRazorComponents().AddInteractiveServerComponents()`, cascading auth state; `HostOptions.ShutdownTimeout = 30 min`.
- Auth: cookie scheme (`Quasar.Auth`, 12 h sliding) + Steam OpenID; `AddAuthorization` with 8 named policies; Data Protection keyring persisted to `MagnetarPaths.GetQuasarDataProtectionKeyringDirectory()`.
- UI/infra: `AddHttpClient`, local storage, `AddApexCharts`, `AddMudServices` (snackbar bottom-start, no dupes, newest-on-top).
- Options singletons + RBAC: `WebServiceOptions`, `ManagedRuntimeOptions`, `QuasarAuthOptions`, `AnalyticsStoreOptions`, `RbacConfigCatalog`, `QuasarRoleMapper`, `TrustedNetworkEvaluator`.
- Core services: `KnownPlayerCatalog`, `MetricsStoreService` (+hosted), `AnalyticsSeriesService`, `ProfilerStoreService`, `AgentRegistry`, `EntityService`, config catalogs (`QuasarConfigProfileCatalog`, `QuasarDevFolderCatalog`, `QuasarWorldTemplateCatalog`, `QuasarPluginCatalogService`, `PluginCatalogRefreshService` (+hosted)), `SteamWorkshopCredentialsCatalog`, `QuasarWorkshopModResolver`.
- Managed runtime + server supervision: `ManagedDedicatedServerRuntimeResolver`, `ManagedRuntimeWarmupService` (+hosted), `DedicatedServerCatalog`, `DedicatedServerSupervisor` (+hosted), `DedicatedServerRuntimePreparer`.
- Web/agent: `FileBrowserService`, `WebServiceState`, `PluginLogStream`, `PluginConfigService` (+hosted), `AgentSocketHandler`, `WebServiceManifestHostedService` (hosted).
- Discord: options/rate-limiter/death-messages catalogs, command dispatcher+router, chat/death/simspeed-alert/log/analytics services, `DiscordBotService` (+hosted).
- Branding/theme/shutdown/update: `BrandingService`, `ThemePreferenceService` (scoped), `QuasarShutdownService`, `QuasarUpdateService` (+hosted).
- Backup: `QuasarBackupSettingsService`, `QuasarBackupService`, `AutomaticBackupService` (+hosted).

### Authentication / Authorization
- Default scheme `QuasarAuthSchemes.Cookie`; challenge = Steam OpenID. On Steam authenticated, `QuasarRoleMapper` validates the Steam ID (extracted from identifier/principal); disallowed → ticket cleared; otherwise claims normalized and role claims added.
- Policies: `CanView` (Viewer/Editor/Admin); `CanEditConfigs`, `CanEditServers`, `CanControlServers`, `CanManageDiscord`, `CanManageAppearance` (Editor/Admin); `CanManageSecurity`, `CanShutdownQuasar` (Admin only).

### Middleware + endpoints
Pipeline: exception handler (prod) → status-code re-execute (`/not-found`) → `UseWebSockets` (30 s keep-alive) → `UseAuthentication` → inline trusted-network principal injection → `UseAuthorization` → `UseAntiforgery`.
Endpoints: `GET /api/health` (status/worker/host/version/baseUrl/connectedAgents/configuredServers/runningServers), `GET /api/discovery` (manifest), `GET /api/analytics/series` (browser-fetched chart series), `GET /api/servers/{uniqueName}/logs/server/download` (streams the newest `SpaceEngineersDedicated*.log` for a configured server), `GET /api/servers/{uniqueName}/logs/magnetar/download` (streams that server's Magnetar `info.log`) — log downloads require `CanView` when auth is enabled, `GET /login` (Steam challenge or unavailable page), `GET /logout`, `GET /access-denied` (standalone branded 403 page for authenticated users lacking a Quasar role), `POST /api/internal/drain` (launcher-token + trusted-network gated; `delaySeconds`/`stopServers` params), `GET /api/backup/download` (`QuasarBackupService.CreateBackup` → streams a fresh ZIP), `GET /api/backup/download/{name}` (downloads an existing backup by name from the Backups dir) — both `RequireAuthorization(CanManageSecurity)` when auth enabled, `Map /ws/agent` → `AgentSocketHandler`, `MapStaticAssets()`, `/branding` physical static files, `MapRazorComponents<App>()` (interactive server; `RequireAuthorization(CanView)` when auth enabled).

### POSIX signals + helpers
On Linux/macOS, SIGINT/SIGTERM handlers either `StopApplication` (when preserving managed servers) or `QuasarShutdownService.ShutdownAsync`. Helpers: `DownloadLogFile`, `ResolveLatestDedicatedServerLogPath`, `ResolveMagnetarInfoLogPath`, `CompositeDisposable`, `EmptyDisposable`, `SanitizeReturnUrl`, `ExtractSteamId`, `AddOrReplaceClaim`, `ShouldUseSourceStaticWebAssets`, `ShouldListenOnAnyInterface`.

## Dependencies
- `Quasar/Components/App.razor` (root component)
- `Quasar/Services/AgentRegistry.cs`, [`Quasar/Services/AgentSocketHandler.cs`](Services/AgentSocketHandler.cs.md), `Quasar/Services/EntityService.cs`, `Quasar/Services/PluginLogStream.cs`, `Quasar/Services/PluginConfigService.cs`
- `Quasar/Services/Auth/*` (`QuasarAuthOptions`, `QuasarRoleMapper`, `RbacConfigCatalog`, `TrustedNetworkEvaluator`, `QuasarAuthSchemes`, `QuasarClaimTypes`, `QuasarPolicyNames`, `QuasarRoles`)
- `Quasar/Services/DedicatedServerCatalog.cs`, `DedicatedServerSupervisor.cs`, `DedicatedServerRuntimePreparer.cs`, `QuasarShutdownService.cs`, `WebServiceState.cs`, `WebServiceOptions.cs`, `WebServiceManifestHostedService.cs`, `ManagedRuntimeOptions.cs`
- `Quasar/Services/Analytics/*`, `Quasar/Services/Discord/*`, `Quasar/Services/PluginSdk/QuasarPluginCatalogService.cs`
- `Quasar/Services/Backup/*` (`QuasarBackupService`, `QuasarBackupSettingsService`, `AutomaticBackupService`)
- `Quasar/Models/DedicatedServerProcessState.cs`
- [`Magnetar.Protocol/Runtime/MagnetarPaths.cs`](../Magnetar.Protocol/Runtime/MagnetarPaths.cs.md)
- External: `ApexCharts`, `AspNet.Security.OpenId.Steam`, `MudBlazor`, `NLog`, Blazored LocalStorage, ASP.NET Core Data Protection

## Notes
- `/api/internal/drain` requires both a matching `X-Quasar-Launcher-Token` header and trusted-network origin — it is the Bootstrap launcher's drain/shutdown hook; `stopServers` distinguishes draining only the Quasar process vs. taking managed servers down first via `QuasarShutdownService`.
- The trusted-network bypass middleware runs after `UseAuthentication` and injects an operator principal for trusted-origin requests, allowing access without Steam login.
- Branding uploads are served via `PhysicalFileProvider` at `/branding` (outside the build-time static-asset manifest). The standalone auth fallback pages embed their own CSS and static Quasar logo/favicon because they run outside the Blazor shell.
- `/api/health`'s "running" count includes `Starting`, `Running`, `Restarting`, and `Stopping` states.
- `/api/analytics/series` requires `CanView` when auth is enabled and is fetched outside the Blazor circuit to keep analytics payloads off SignalR. The endpoint returns both scalar and profiler-backed charts using the same response shape.
