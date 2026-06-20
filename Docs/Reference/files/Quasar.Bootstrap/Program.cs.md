# Quasar.Bootstrap/Program.cs

**Module:** Quasar.Bootstrap  **Kind:** class  **Tier:** 1

## Summary
Entry point and core logic for the Quasar launcher. It implements three CLI commands (`ensure-running`, `serve`, `activate-release`) and the supporting types `BootstrapOptions` (host/port + update + preserve-servers policy from `appsettings.json`) and `LauncherCoordinator` (an `IHostedService` that supervises the Quasar worker process, watches the active-release pointer and Bootstrap update request files, downloads an initial UI worker from the UI release stream when needed, hot-reloads activated UI releases, and self-upgrades the launcher from the primary Quasar release stream).

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
- Reads the `Quasar` (fallback `MagnetarWeb`) config section from `appsettings.json` / `appsettings.{env}.json`, searched in `AppContext.BaseDirectory`, a `WebService` sibling, the Quasar data directory (`MagnetarPaths.GetQuasarDirectory()`), and up to 8 ancestor `Quasar/` source dirs.
- Properties: `Host` (default `127.0.0.1`), `AdvertisedHost` (remaps `0.0.0.0`/`*`/`+` → `127.0.0.1`), `Port` (default 8080), `PreserveServersOnShutdown` (default true; env `QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN` or `PreserveManagedServersOnShutdown`), update owner/repository/prerelease settings, `LinuxWebAssetName`/`LinuxBootstrapAssetName` (Linux defaults), `WindowsWebAssetName`/`WindowsBootstrapAssetName` (Windows defaults), computed `WebAssetName`/`BootstrapAssetName` (OS-selected), `UpdatesCheckInterval`, Bootstrap `Version`, `BaseUrl`, `ListenUrl`; const `SupervisorName = "Quasar"`.

### `LauncherCoordinator` (IHostedService, IDisposable)
| Member | Description |
|---|---|
| `IsReady` / `GetHealthPayload()` / `GetManifest()` | Worker liveness, health summary object (status/workerId/hostId/hostName/baseUrl/active worker version+url), and `WebServiceDiscoveryManifest`. |
| `StartAsync` | Creates dirs, downloads an initial UI worker into `ManagedRuntime/WebService/<version>` when no packaged/active worker exists, ensures an active-release pointer exists, activates it, starts `FileSystemWatcher`s on the active pointer and Bootstrap update request file. |
| `StopAsync` | Sets `_isStopping`, stops the pointer watcher, Bootstrap update request watcher, and launcher update monitor, drains/retires the current worker, stopping managed servers only when `!PreserveServersOnShutdown`. |
| `StartBootstrapUpdateMonitor` / `RunBootstrapUpdateMonitorAsync` | Starts the self-upgrade loop on Linux and Windows when updates are enabled; checks after 30 s and then every configured update interval. |
| `StartWatchingBootstrapUpdateRequests` / `QueueBootstrapUpdateRequest` | Watches `Updates/bootstrap-update-request.json`, debounces file events, deletes the request, logs the admin-triggered activation, and calls the same Bootstrap self-upgrade path immediately. |
| `TryUpgradeBootstrapAsync` / `ResolveBootstrapPayloadDirectory` / `ApplyBootstrapUpdate` | Serializes self-upgrade attempts, finds an actually newer non-draft release containing the platform `BootstrapAssetName`, verifies `SHA256SUMS`, extracts it, accepts either a flat launcher archive or one single top-level installer directory, skips drain/restart if the downloaded launcher is byte-identical to the installed launcher, preserves existing `appsettings.json`, replaces launcher files, drains the UI worker without stopping managed servers, then restarts: on Linux exits with code 75 so systemd restarts the updated launcher; on Windows spawns a detached `Quasar.exe serve --quiet` and exits 0 (Scheduled Task restart-on-failure is the safety net). |
| `IsReleasePointerUsable` / `IsKnownReleasePath` | Validates active-release pointers. In service mode, Bootstrap rejects stale pointers to arbitrary external build directories and only trusts packaged `WebService/`, managed web releases, staged legacy updates, or explicit environment-configured worker paths. |
| `ActivateCurrentReleaseAsync` | Under `_activationLock`: drains the current worker without stopping managed servers, starts the new worker, waits for `/api/health` (60 s), swaps it in, then prunes inactive managed web-release directories. |
| `StartWorkerAsync` | Copies install-directory `appsettings.json` into the worker directory, launches the worker with env vars (`QUASAR_MODE=service`, `QUASAR_LAUNCHER_TOKEN`, `QUASAR_BOOTSTRAP_VERSION`, `QUASAR_INSTALL_DIR`, `QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN`, foreground console-logging), and pumps stdout/stderr in foreground. |
| `SyncInstallAppSettingsToWorker` | Keeps the managed worker's base `appsettings.json` aligned with the stable launcher/install directory before process start. |
| `DrainAndRetireWorkerAsync` | POSTs `/api/internal/drain?delaySeconds=&stopServers=` with `X-Quasar-Launcher-Token`, waits for exit, force-kills on timeout. |
| `HandleWorkerExited` | On unexpected worker exit (not stopping), restarts via `ActivateCurrentReleaseAsync(force: true)`. |
| `TryConsumeLauncherShutdownRequest` | Detects and deletes the worker-written `launcher-shutdown-request` file, allowing UI-driven Quasar shutdown to exit Bootstrap without restarting the worker. |
| `TryConsumeBootstrapUpdateRequest` | Detects and deletes the worker-written Bootstrap update request file before running immediate self-upgrade. |
| `HandleReleasePointerChanged` | Debounces pointer file changes 250 ms then re-activates. |
| `TryMigrateStagedActiveRelease` | Migrates legacy active pointers that still target `Updates/Staged/<version>` into `ManagedRuntime/WebService/<version>` before launch. |
| `TryBuildInitialReleasePointer` | Resolves worker by priority: `QUASAR_WEB_EXE`/`MAGNETAR_WEB_EXE` → `QUASAR_WEB_DLL`/`MAGNETAR_WEB_DLL` → packaged `WebService/Quasar(.exe)` → ancestor-walk for `Quasar.dll`/`Quasar.exe`. |

## Dependencies
- `Magnetar.Protocol/Discovery/WebServiceDiscoveryManifest.cs`
- [`Magnetar.Protocol/Runtime/MagnetarPaths.cs`](../Magnetar.Protocol/Runtime/MagnetarPaths.cs.md), `Magnetar.Protocol/Runtime/QuasarActiveReleasePointer.cs`, `Magnetar.Protocol/Runtime/QuasarReleaseVersion.cs`, `Magnetar.Protocol/Runtime/QuasarWebReleaseLayout.cs`
- `Microsoft.Extensions.Configuration` / `.Hosting` / `.Logging`
- `System.Text.Json` (manifest + pointer), `System.Net.Sockets` (`TcpClient` port check)

## Notes
- The `Quasar.Bootstrap` named mutex serializes spawn attempts across processes on a machine.
- `IsCurrentBootstrapAssembly` / `IsCurrentBootstrapExecutable` prevent pointing the worker at the bootstrap itself; RID-targeted DLL paths are rejected when no sibling `runtimeconfig.json` exists (avoids libhostpolicy failures from the `obj/` tree).
- `PreserveServersOnShutdown` and `QUASAR_INSTALL_DIR` are propagated to the worker so the launcher and worker agree on shutdown policy and the update service can sync resolved appsettings back to the stable install directory. A worker-written `launcher-shutdown-request` file lets Bootstrap exit cleanly for full Quasar shutdown while preserving servers; a worker-written `Updates/bootstrap-update-request.json` file lets the Updates page ask Bootstrap to run launcher self-update immediately.
- Initial UI worker download scans GitHub releases for the newest non-draft release containing the configured UI asset (`WebAssetName`, OS-selected), extracts it into `ManagedRuntime/WebService/<version>`, and validates the extracted web layout before activation; launcher self-upgrade scans the primary Quasar release stream for the platform `BootstrapAssetName` and compares against the normalized release identity, not raw `AssemblyVersion`. Launcher self-upgrade strips the single `quasar-installer-*` archive directory when present, while still accepting older flat launcher archives. If version metadata is stale but the installed launcher already matches the downloaded update byte-for-byte, Bootstrap logs and skips the worker drain/restart instead of repeating the same self-update every check. Both periodic and request-file-triggered Bootstrap updates share a semaphore and the same verified install path. Both scans honor `Quasar:Updates:IncludePrerelease`, including the data-directory override written by the Updates page after Bootstrap restarts. Service-mode active-release pointer validation prevents a previously written local `bin/Debug` worker path from overriding the installed packaged worker.
