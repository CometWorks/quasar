# Quasar.Bootstrap/Program.cs

**Module:** Quasar.Bootstrap  **Kind:** class  **Tier:** 1

## Summary
`Program.cs` is the entry point and core logic for the `Quasar.Bootstrap` launcher. It implements three CLI commands (`ensure-running`, `serve`, `activate-release`) and two supporting types: `BootstrapOptions` (reads host/port from `appsettings.json`) and `LauncherCoordinator` (`IHostedService` that manages the Quasar worker process, watches the active-release pointer file, and handles zero-downtime hot-reload when a new release is activated).

## Structure
**Namespace:** `Quasar.Bootstrap`  
**Top-level types:** `Program` (internal static), `BootstrapOptions` (internal sealed), `LauncherCoordinator` (internal sealed, implements `IHostedService`, `IDisposable`), `LauncherForegroundOptions` (sealed record), `WorkerProcessHandle` (sealed record)

### `Program` (static)
| Member | Description |
|---|---|
| `Main(string[] args)` | Parses flags (`--quiet`, `--open-browser`, `--force`, `--foreground`/`--console`) and dispatches to command handlers |
| `EnsureRunningAsync` | Checks health; acquires `Quasar.Bootstrap` mutex to prevent concurrent spawns; checks port availability; in foreground mode calls `ServeAsync` directly; otherwise spawns a detached `serve` process and polls for health (60 attempts × 1 s) |
| `ServeAsync` | Starts `LauncherCoordinator`; blocks until Ctrl+C; handles clean stop |
| `ActivateReleaseAsync` | Writes a `QuasarActiveReleasePointer` JSON file; calls `EnsureRunningAsync` to guarantee the launcher is running |
| `KillExistingServerAsync` | Reads manifest PID, calls `Process.Kill`, polls for health to disappear (15 s) |
| `TryGetHealthyServiceUriAsync` | Reads discovery manifest, HTTP-GETs `/api/health`, returns `Uri?` on 200 |
| `TryBuildBootstrapLaunchSpec` | Determines how to re-spawn itself as a detached `serve` worker; prefers `dotnet <assembly>` when DLL + runtimeconfig exist; guards against the dotnet host without a valid assembly path |
| `IsHeadless` | Returns true on Linux/macOS when neither `DISPLAY` nor `WAYLAND_DISPLAY` is set |
| `TryOpenBrowser` | Cross-platform best-effort browser open (xdg-open / gio / sensible-browser on Linux) |

### `BootstrapOptions` (sealed)
- Reads `Quasar` (or fallback `MagnetarWeb`) section from `appsettings.json` / `appsettings.{env}.json` searched up to 8 parent directories from `AppContext.BaseDirectory` plus a `WebService` sibling.
- Properties: `Host` (default `127.0.0.1`), `AdvertisedHost` (remaps `0.0.0.0`/`*`/`+` to `127.0.0.1`), `Port` (default 58631), `BaseUrl`, `ListenUrl`
- `SupervisorName` constant: `"Quasar"`

### `LauncherCoordinator` (IHostedService, IDisposable)
| Member | Description |
|---|---|
| `IsReady` | True if current worker process has not exited |
| `GetHealthPayload()` | Returns anonymous object with status, workerId, hostId, hostName, baseUrl, active worker version/URL |
| `GetManifest()` | Builds `WebServiceDiscoveryManifest` (written to disk by the caller) |
| `StartAsync` | Creates directories, ensures active-release pointer exists, activates current release, starts `FileSystemWatcher` on the pointer file |
| `StopAsync` | Sets `_isStopping`, drains and retires current worker with `stopManagedServers: true` |
| `ActivateCurrentReleaseAsync` | Protected by `_activationLock`; starts new worker process, waits for `/api/health` (60 s), then gracefully drains old worker (20 s grace + 30 s kill timeout) |
| `StartWorkerAsync` | Launches worker process with env vars (`QUASAR_MODE=service`, `QUASAR_LAUNCHER_TOKEN`, etc.); in foreground mode pumps stdout/stderr to console |
| `HandleWorkerExited` | If the current worker exits unexpectedly, triggers `ActivateCurrentReleaseAsync(force: true)` restart |
| `HandleReleasePointerChanged` | Debounces file-change events by 250 ms, then re-activates |
| `DrainAndRetireWorkerAsync` | POSTs `/api/internal/drain` with `X-Quasar-Launcher-Token`, waits for clean exit, force-kills on timeout |
| `TryBuildInitialReleasePointer` | Searches for Quasar worker in priority: `QUASAR_WEB_EXE`/`MAGNETAR_WEB_EXE` env → `QUASAR_WEB_DLL`/`MAGNETAR_WEB_DLL` → `WebService/` sibling → directory-walk for `Quasar.dll`/`Quasar.exe` |

## Dependencies
- `Magnetar.Protocol.Discovery` — `WebServiceDiscoveryManifest`
- `Magnetar.Protocol.Runtime` — `MagnetarPaths`, `QuasarActiveReleasePointer`
- `Microsoft.Extensions.Configuration` — `IConfigurationRoot`, `ConfigurationBuilder`
- `Microsoft.Extensions.Hosting` — `IHostedService`
- `Microsoft.Extensions.Logging` — `ILogger`, `LoggerFactory`
- `System.Text.Json` — manifest and pointer serialization
- `System.Net.Sockets` — `TcpClient` for port-in-use check

## Notes
- The `Quasar.Bootstrap` named mutex prevents multiple concurrent spawn attempts from different processes on the same machine.
- Port-in-use check (`TcpClient.Connect`) runs before spawning to give a clear error rather than a silent EADDRINUSE crash.
- `IsCurrentBootstrapAssembly` / `IsCurrentBootstrapExecutable` guards prevent the coordinator from pointing the worker at itself.
- On RID-targeted builds, the bootstrap checks for a sibling `runtimeconfig.json` before using a DLL path — this avoids libhostpolicy errors when invoked from the `obj/` intermediate tree.
- Windows/Linux cross-platform: browser open, process names, and RID publish are all handled.
- `GetHealthPayload` uses `hostId`/`hostName` (not `nodeId`/`nodeName`), reflecting the Node→Host rename in the manifest.
