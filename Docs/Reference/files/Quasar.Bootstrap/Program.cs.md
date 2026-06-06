# Quasar.Bootstrap/Program.cs

**Module:** Quasar.Bootstrap  **Kind:** class  **Tier:** 1

## Summary
Entry point and core logic for the `Quasar.Bootstrap` ensure-running helper. It implements three CLI commands (`ensure-running`, `serve`, `activate-release`) and the supporting types `BootstrapOptions` (host/port + preserve-servers policy from `appsettings.json`) and `LauncherCoordinator` (an `IHostedService` that supervises the Quasar worker process, watches the active-release pointer file, and performs zero-downtime hot-reload when a new release is activated).

## Structure
**Namespace:** `Quasar.Bootstrap`  
**Top-level types:** `Program` (internal static), `BootstrapOptions` (internal sealed), `LauncherCoordinator` (internal sealed, `IHostedService`/`IDisposable`), `LauncherForegroundOptions` (sealed record), `WorkerProcessHandle` (sealed record nested in coordinator).

### `Program` (static)
| Member | Description |
|---|---|
| `Main(args)` | Parses flags (`--quiet`, `--open-browser`, `--force`, `--foreground`/`--console`), picks command (default `ensure-running`), decides foreground vs detached based on an attached interactive console. |
| `EnsureRunningAsync` | Returns existing healthy service (or `--force`-kills it); acquires `Quasar.Bootstrap` named mutex; fails fast if the port is bound by a non-Quasar process; in foreground runs `ServeAsync` directly (optionally opening a browser once healthy); otherwise spawns a detached `serve` process and polls health (60×1 s). |
| `ServeAsync` | Builds a `LauncherCoordinator`, starts it, blocks on Ctrl+C, then drains/stops. |
| `ActivateReleaseAsync` | Writes a `QuasarActiveReleasePointer` (from `--file`/`--working-dir`/`--args`/`--version`), then `EnsureRunningAsync`. |
| `KillExistingServerAsync` | Kills the manifest PID, waits up to 15 s for `/api/health` to stop responding. |
| `TryGetHealthyServiceUriAsync` | Reads discovery manifest, GETs `/api/health`, returns `Uri?` on success. |
| `TryBuildBootstrapLaunchSpec` | Chooses how to re-spawn the worker; prefers `dotnet <assembly>` when a DLL + sibling `runtimeconfig.json` exist; guards against the dotnet host without a valid assembly. |
| `IsHeadless` / `TryOpenBrowser` / `TryStartBrowserCommand` | Display-server detection + cross-platform best-effort browser launch (Linux: xdg-open/gio/sensible-browser). |
| `StartDetachedProcess` | Spawns the detached worker with redirected (drained) stdout/stderr so output does not bleed into the parent terminal. |

### `BootstrapOptions` (sealed)
- Reads the `Quasar` (fallback `MagnetarWeb`) config section from `appsettings.json` / `appsettings.{env}.json`, searched in `AppContext.BaseDirectory`, a `WebService` sibling, and up to 8 ancestor `Quasar/` source dirs.
- Properties: `Host` (default `127.0.0.1`), `AdvertisedHost` (remaps `0.0.0.0`/`*`/`+` → `127.0.0.1`), `Port` (default 58631), `PreserveServersOnShutdown` (default true; env `QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN` or `PreserveManagedServersOnShutdown`), `BaseUrl`, `ListenUrl`; const `SupervisorName = "Quasar"`.

### `LauncherCoordinator` (IHostedService, IDisposable)
| Member | Description |
|---|---|
| `IsReady` / `GetHealthPayload()` / `GetManifest()` | Worker liveness, health summary object (status/workerId/hostId/hostName/baseUrl/active worker version+url), and `WebServiceDiscoveryManifest`. |
| `StartAsync` | Creates dirs, ensures an active-release pointer exists, activates it, starts a `FileSystemWatcher` on the pointer. |
| `StopAsync` | Sets `_isStopping`, drains/retires the current worker, stopping managed servers only when `!PreserveServersOnShutdown`. |
| `ActivateCurrentReleaseAsync` | Under `_activationLock`: starts the new worker, waits for `/api/health` (60 s), swaps it in, then drains the previous worker (20 s grace). |
| `StartWorkerAsync` | Launches the worker with env vars (`QUASAR_MODE=service`, `QUASAR_LAUNCHER_TOKEN`, `QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN`, foreground console-logging); pumps stdout/stderr in foreground. |
| `DrainAndRetireWorkerAsync` | POSTs `/api/internal/drain?delaySeconds=&stopServers=` with `X-Quasar-Launcher-Token`, waits for exit, force-kills on timeout. |
| `HandleWorkerExited` | On unexpected worker exit (not stopping), restarts via `ActivateCurrentReleaseAsync(force: true)`. |
| `HandleReleasePointerChanged` | Debounces pointer file changes 250 ms then re-activates. |
| `TryBuildInitialReleasePointer` | Resolves worker by priority: `QUASAR_WEB_EXE`/`MAGNETAR_WEB_EXE` → `QUASAR_WEB_DLL`/`MAGNETAR_WEB_DLL` → packaged `WebService/Quasar(.exe)` → ancestor-walk for `Quasar.dll`/`Quasar.exe`. |

## Dependencies
- `Magnetar.Protocol/Discovery/WebServiceDiscoveryManifest.cs`
- [`Magnetar.Protocol/Runtime/MagnetarPaths.cs`](../Magnetar.Protocol/Runtime/MagnetarPaths.cs.md), `Magnetar.Protocol/Runtime/QuasarActiveReleasePointer.cs`
- `Microsoft.Extensions.Configuration` / `.Hosting` / `.Logging`
- `System.Text.Json` (manifest + pointer), `System.Net.Sockets` (`TcpClient` port check)

## Notes
- The `Quasar.Bootstrap` named mutex serializes spawn attempts across processes on a machine.
- `IsCurrentBootstrapAssembly` / `IsCurrentBootstrapExecutable` prevent pointing the worker at the bootstrap itself; RID-targeted DLL paths are rejected when no sibling `runtimeconfig.json` exists (avoids libhostpolicy failures from the `obj/` tree).
- `PreserveServersOnShutdown` is propagated to the worker so the launcher and worker agree on whether detached Magnetars stay running across a Quasar restart.
