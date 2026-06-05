# Quasar/Program.cs

**Module:** Quasar.Host  **Kind:** class  **Tier:** 1

## Summary
The ASP.NET Core / Blazor Server entry point for the Quasar supervisor host. `Program.Main` builds the `WebApplication`, registers all DI services, configures authentication and authorization, wires middleware, maps HTTP/WebSocket endpoints, and runs the application. It is the system wiring hub — every significant service in the process is registered here.

## Structure
Namespace: `Quasar`

**`Program`** — public class with a single `static void Main(string[] args)` entry point.

### Configuration loading
`AddDeploymentConfigurationSources` walks up to 8 parent directories probing for `appsettings.json` / `appsettings.{env}.json`, in addition to `AppContext.BaseDirectory`, `Directory.GetCurrentDirectory()`, and a `WebService/` subdirectory. Environment variables and command-line args are also added. This allows running from a dev worktree or a deployed `~/Documents/Quasar` folder without copying config files.

Three strongly-typed options objects are read up-front from `IConfiguration`:
- `WebServiceOptions` (`Quasar.Host` / listen host+port, launcher token, etc.)
- `ManagedRuntimeOptions` (SteamCmd, Magnetar, DS install paths)
- `QuasarAuthOptions` (auth enabled flag, Steam, OIDC, trusted-network bypass, Workshop settings)

### Kestrel binding
If `ASPNETCORE_URLS` is not set, Kestrel binds to `{host}:{port}` from `WebServiceOptions`; wildcard hosts (`0.0.0.0`, `[::]`, `*`, `+`) use `ListenAnyIP`.

### DI service registrations (in order)
| Category | Services registered |
|---|---|
| Blazor | `AddRazorComponents` + `AddInteractiveServerComponents`, `AddCascadingAuthenticationState` |
| Host | `HostOptions.ShutdownTimeout = 30 min` |
| Auth | `AddAuthentication` (cookie scheme + Steam OpenId), `AddAuthorization` (8 named policies) |
| Data Protection | Keys persisted to `MagnetarPaths.GetQuasarDataProtectionKeyringDirectory()`, app name `"Quasar"` |
| HTTP | `AddHttpClient`, `AddLocalStorageServices` |
| MudBlazor | `AddMudServices` (snackbar bottom-start, no duplicates, newest-on-top) |
| Options singletons | `WebServiceOptions`, `ManagedRuntimeOptions`, `QuasarAuthOptions` |
| RBAC | `RbacConfigCatalog`, `QuasarRoleMapper`, `TrustedNetworkEvaluator` |
| Player tracking | `KnownPlayerCatalog` |
| Metrics | `MetricsStoreService` (singleton + hosted) |
| Agent | `AgentRegistry`, `EntityService`, `AgentSocketHandler` |
| Config catalogs | `QuasarConfigProfileCatalog`, `QuasarDevFolderCatalog`, `QuasarWorldTemplateCatalog`, `QuasarPluginCatalogService`, `SteamWorkshopCredentialsCatalog`, `QuasarWorkshopModResolver` |
| Managed runtime | `ManagedDedicatedServerRuntimeResolver`, `ManagedRuntimeWarmupService` (singleton + hosted) |
| Server supervision | `DedicatedServerCatalog`, `DedicatedServerSupervisor` (singleton + hosted), `DedicatedServerRuntimePreparer` |
| Misc services | `FileBrowserService`, `WebServiceState`, `PluginLogStream`, `PluginConfigService` (singleton + hosted) |
| Web manifest | `WebServiceManifestHostedService` (hosted) |
| Discord | `DiscordOptionsCatalog`, `DiscordRateLimiter`, `DeathMessagesCatalog`, `DiscordCommandDispatcher`, `DiscordCommandRouter`, `DiscordChatRelayService`, `DiscordDeathRelayService`, `DiscordLogRelayService`, `DiscordAnalyticsExportService`, `DiscordBotService` (singleton + hosted) |
| Branding / Theme | `BrandingService` (singleton), `ThemePreferenceService` (scoped) |
| Shutdown | `QuasarShutdownService` (singleton) |

### Authentication / Authorization
- Default scheme: `QuasarAuthSchemes.Cookie` (cookie name `"Quasar.Auth"`, 12 h sliding expiry, `HttpOnly`, `SameSite=Lax`)
- Challenge scheme: Steam OpenID via `AspNet.Security.OpenId.Steam`
- On Steam authenticated: `QuasarRoleMapper` validates the Steam ID; if allowed, claims are normalised and roles added
- 8 authorization policies:

| Policy | Roles allowed |
|---|---|
| `CanView` | Viewer, Editor, Admin |
| `CanEditConfigs` | Editor, Admin |
| `CanEditServers` | Editor, Admin |
| `CanControlServers` | Editor, Admin |
| `CanManageDiscord` | Editor, Admin |
| `CanManageAppearance` | Editor, Admin |
| `CanManageSecurity` | Admin only |
| `CanShutdownQuasar` | Admin only |

### Middleware pipeline
`UseExceptionHandler("/Error")` (production only) → `UseStatusCodePagesWithReExecute("/not-found")` → `UseWebSockets(keepAlive=30s)` → `UseAuthentication` → inline trusted-network bypass middleware → `UseAuthorization` → `UseAntiforgery`

### Endpoint mapping
| Route | Method | Auth | Description |
|---|---|---|---|
| `/api/health` | GET | — | JSON health summary: status, workerId, hostId, hostName, version, baseUrl, connectedAgents, configuredServers, runningServers |
| `/api/discovery` | GET | — | JSON web service manifest (`WebServiceState.CurrentManifest`) |
| `/login` | GET | Anonymous | Redirects to Steam OpenID challenge; shows error page if Steam not configured |
| `/logout` | GET | Anonymous | Signs out cookie and redirects to `/` |
| `/access-denied` | GET | Anonymous | Static HTML access-denied page |
| `/api/internal/drain` | POST | Launcher token + trusted network | Initiates graceful drain/shutdown; supports `delaySeconds` and `stopServers` query params |
| `/ws/agent` | MAP | — | WebSocket handler for Quasar.Agent connections (`AgentSocketHandler`) |
| Static assets | — | — | `MapStaticAssets()` for build-time assets; `UseStaticFiles` on `/branding` path for runtime-uploaded logos/favicon |
| Razor components | — | `CanView` if auth enabled | `MapRazorComponents<App>().AddInteractiveServerRenderMode()` |

### POSIX signal handling
On Linux/macOS, `SIGINT` and `SIGTERM` are intercepted via `PosixSignalRegistration`. Both delegate to `QuasarShutdownService.ShutdownAsync` (stops managed DS instances gracefully) before calling `IHostApplicationLifetime.StopApplication`.

### Helper types
- `CompositeDisposable` — disposes multiple `IDisposable` objects
- `EmptyDisposable` — no-op singleton disposable
- `SanitizeReturnUrl` — rejects absolute/protocol-relative URLs
- `ExtractSteamId` — parses 17-digit Steam ID from OpenID URL or plain string
- `AddOrReplaceClaim` — idempotent claim mutation
- `ShouldUseSourceStaticWebAssets` — probes ancestor directories for `Quasar.csproj` to enable source static assets in dev

## Dependencies
- `Quasar/Components/App.razor` (root Blazor component)
- [`Quasar/Models/DedicatedServerProcessState.cs`](Models/DedicatedServerProcessState.cs.md)
- [`Quasar/Services/AgentRegistry.cs`](Services/AgentRegistry.cs.md), `AgentSocketHandler.cs`, `EntityService.cs`
- `Quasar/Services/Auth/QuasarAuthOptions.cs`, `QuasarRoleMapper.cs`, `RbacConfigCatalog.cs`, `TrustedNetworkEvaluator.cs`, `QuasarAuthSchemes.cs`, `QuasarClaimTypes.cs`, `QuasarPolicyNames.cs`, `QuasarRoles.cs`
- `Quasar/Services/BrandingService.cs`, `ThemePreferenceService.cs`
- [`Quasar/Services/DedicatedServerCatalog.cs`](Services/DedicatedServerCatalog.cs.md), `DedicatedServerSupervisor.cs`, `DedicatedServerRuntimePreparer.cs`
- `Quasar/Services/Discord/*` (all Discord relay/bot services)
- `Quasar/Services/FileBrowserService.cs`
- [`Quasar/Services/KnownPlayerCatalog.cs`](Services/KnownPlayerCatalog.cs.md)
- [`Quasar/Services/ManagedDedicatedServerRuntimeResolver.cs`](Services/ManagedDedicatedServerRuntimeResolver.cs.md), `ManagedRuntimeWarmupService.cs`
- `Quasar/Services/MetricsStoreService.cs`
- `Quasar/Services/PluginConfigService.cs`, `PluginLogStream.cs`
- `Quasar/Services/PluginSdk/QuasarPluginCatalogService.cs`
- `Quasar/Services/QuasarConfigProfileCatalog.cs`, `QuasarDevFolderCatalog.cs`, `QuasarWorldTemplateCatalog.cs`
- `Quasar/Services/QuasarLoggingConfigurator.cs`
- [`Quasar/Services/QuasarShutdownService.cs`](Services/QuasarShutdownService.cs.md)
- `Quasar/Services/SteamWorkshopCredentialsCatalog.cs`, `QuasarWorkshopModResolver.cs`
- [`Quasar/Services/WebServiceManifestHostedService.cs`](Services/WebServiceManifestHostedService.cs.md), `WebServiceOptions.cs`, `WebServiceState.cs`
- `Quasar/Services/ManagedRuntimeOptions.cs`
- `Magnetar.Protocol` (`MagnetarPaths`)
- External: `AspNet.Security.OpenId.Steam`, `MudBlazor`, `NLog`, `Blazor.LocalStorage`

## Notes
- The `/api/internal/drain` endpoint requires both a `X-Quasar-Launcher-Token` header match and trusted-network origin — this is the Quasar Bootstrap launcher's shutdown/update hook. The `stopServers` query param distinguishes between stopping only the Quasar process (drain) vs. stopping all managed servers first via `QuasarShutdownService.ShutdownAsync`.
- The trusted-network bypass middleware runs after `UseAuthentication` and injects an auto-generated principal for requests from loopback/same-subnet addresses (configurable), allowing operator access without Steam login.
- Static asset serving for branding uploads uses `PhysicalFileProvider` pointed at `MagnetarPaths.GetQuasarBrandingDirectory(webRootPath)` under the `/branding` request path, which is outside the build-time static asset manifest.
- `BlazorDisableThrowNavigationException=true` is set in the csproj to suppress the Blazor navigation exception that would otherwise bubble as an error.
- `/api/health` counts "running" servers as those in `Starting`, `Running`, `Restarting`, or `Stopping` states.
