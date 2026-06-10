# Quasar/Services/DedicatedServerSupervisor.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`DedicatedServerSupervisor` is the heart of Quasar's process management. It is an `IHostedService` that maintains in-memory `ManagedServerState` for every configured dedicated server, runs a 2-second reconcile loop that starts/stops/restarts processes to match goal state, evaluates server health (agent heartbeat, simulation frame progress, uptime thresholds), rotates and prunes Quasar-captured DS stdout/stderr logs, captures mod-download failure lines for dashboard surfacing, persists runtime state across Quasar worker restarts and **adopts surviving detached processes by PID on startup**, and coordinates graceful stop (save + stop commands to the agent before kill) plus scheduled and maximum-uptime restarts.

## Structure

Namespace: `Quasar.Services`

**`DedicatedServerSupervisor`** — sealed class implementing `IHostedService`, `IDisposable`.

| Member | Description |
|---|---|
| `event Action? Changed` | Raised after any state change; `NotifyChanged` also schedules a debounced state persist. |
| `StartAsync(ct)` | Syncs definitions from catalog, restores persisted state (adopting live PIDs), subscribes to catalog and managed-runtime warmup `Changed`, launches the reconcile loop, persists. |
| `StopAsync(ct)` | If `_preserveManagedServersOnShutdown` (default), only persists a snapshot and leaves servers running; otherwise stops all running servers then persists. |
| `GetSnapshots()` | Cloned `DedicatedServerRuntimeSnapshot` per server, merged with current agent connectivity. |
| `bool HealthMonitoringDisabled` | `=> _options.DisableServerHealthMonitoring` — instance-wide flag surfaced once at the top of the Dashboard (`Home.razor`) instead of per-card. |
| `SetGoalStateAsync(...)` | Delegates to catalog then reconciles immediately. The 3-arg overload delegates to a `(…, bool reconcile)` overload with `reconcile:true`; callers driving their own graceful stop (e.g. `QuasarShutdownService` shutting down all servers) pass `reconcile:false` to record intent without a competing reconcile-driven stop. |
| `StartServerAsync(...)` | Guarded by `StartInProgress`; blocks launch until `ManagedRuntimeWarmupService.IsReady`, resolves runtime, prepares files, rotates the active stdout/stderr log slot, spawns the process with full env vars, prepends Linux native-library search paths to `LD_LIBRARY_PATH` when the resolver found them, optionally logs launch diagnostics when the server definition enables `LogLaunchEnvironment`, applies startup priority, starts stdout/stderr pumps. |
| `StopServerAsync(uniqueName, forceAfter?, ct)` | Sends `SaveWorld` + `StopServer` to the agent, waits for exit, kills the process tree if the grace window expires. |
| `RestartServerAsync(...)` | Sets goal On + AutoStart, stops, starts. |
| `BeginLauncherDrain()` | Sets `_preserveManagedServersOnShutdown = true` and persists synchronously — called before a worker-only restart so the next worker can re-adopt. |
| `Dispose()` | Cancels the persist-debounce CTS and the shutdown CTS. |

**`ReconcileAsync`** — per server: liveness vs goal state → Start/Stop/Restart; unhealthy auto-restart (`AutoRestartOnUnhealthy`, throttled by `CanScheduleHealthRestart`); maximum-uptime restart; daily scheduled restart; both planned restarts honour `AvoidSimultaneousScheduledRestarts` via `CanRunPlannedRestart`. Also promotes Starting/Restarting → Running once the agent reports `IsRunning`, and applies `ReadyProcessPriority` once healthy. The reconcile loop and start path also apply per-server CPU affinity to the live process.

**`TryApplyCpuAffinity` / `ApplyCpuAffinityCores` / `TryApplyTaskset`** — private methods that apply `DedicatedServerDefinition.CpuAffinity` to the running process. On Windows they set `process.ProcessorAffinity` (`CpuAffinitySpec.ToWindowsMask`); on Linux they run `taskset -a -p -c <cores> <pid>`. Empty affinity releases a previously-pinned process back to all cores. Failed values are recorded in `ManagedServerState.LastFailedCpuAffinity` and not retried each reconcile; the last successfully applied value is tracked in `LastAppliedCpuAffinity`. Both reset on process start; adopted processes treat current affinity as already applied.

**`PrepareServerLogSlot` / `RotateActiveLogIfPresent` / `PruneServerLogFiles`** — manage Quasar-side DS log rotation. On each start, non-empty active `stdout.log` / `stderr.log` files are moved to timestamped archives (`stdout-*.log`, `stderr-*.log`) before a fresh active slot is used. On start and process exit, rotated archives are pruned to the per-server `DsLogFilesToKeep` setting, interpreted as current slot plus archives.

**`LogManagedServerLaunchEnvironment`** — emits a warning-level Quasar log entry before process start when `DedicatedServerDefinition.LogLaunchEnvironment` is enabled. The entry includes the final executable path, arguments, working directory, and sorted environment block, so operators can verify values such as `LD_LIBRARY_PATH` and `QUASAR_AGENT_PROFILER_MODE`; it may expose secrets and is intended for targeted troubleshooting.

**`HandleRuntimeWarmupChanged` / `SetStopped`** — `HandleRuntimeWarmupChanged` kicks a reconcile when startup runtime preparation completes, so auto-started servers blocked during SteamCMD/DS download launch promptly after readiness. `SetStopped` records a non-fault stopped state and user-facing message when launch is requested while prerequisites are still preparing.

**`EvaluateHealth` / `EvaluateSimulationProgress`** — agent connectivity, heartbeat staleness, simulation frame-progress score (frames/sec normalised to 60 Hz), uptime warn/recycle thresholds. Honours `DisableServerHealthMonitoring` and per-definition `EnableHealthMonitoring`. Agent-attach grace counts from `AgentWatchSinceUtc`. When health monitoring is disabled the per-server health message is now an empty string (the Dashboard surfaces the disabled state once via `HealthMonitoringDisabled`).

**`RestorePersistedRuntimeState` / `TryAdoptProcess`** — on startup, `Process.GetProcessById` re-adopts still-running DS processes from a prior worker, re-attaches the `Exited` handler, and resets `AgentWatchSinceUtc` to "now" so the agent gets a fresh reconnect grace; processes no longer alive are marked Stopped.

**`PumpStandardOutputAsync` / `PumpStandardErrorAsync`** — append timestamped lines to the current per-server active log files (`stdout.log`, `stderr.log`) using file sharing that allows start-time rotation. Plugin-SDK JSON lines (`TryParseSinkLine`) are **skipped** here because they now arrive via the agent network relay (`AgentSocketHandler`); only non-plugin output is wrapped as Magnetar-source `PluginLogEntry`. stderr lines log at Error. Both pumps call `RecordModDownloadFailure` so Magnetar/server output that looks like Workshop mod download failure is retained in `DedicatedServerRuntimeSnapshot.ModDownloadFailures`.

Private nested types: `ManagedServerState` (full mutable per-server state incl. `Process`, `StartInProgress`, `AgentWatchSinceUtc`, simulation/priority/scheduled-restart tracking, `ModDownloadFailures`, plus `string? LastAppliedCpuAffinity` / `string? LastFailedCpuAffinity`); `PersistedSupervisorState` / `PersistedManagedServerState` (JSON-serialised subset incl. `ProcessId`); `ReconcileAction` enum; `ServerHealthAssessment` readonly record struct.

## Dependencies

- `Quasar/Services/DedicatedServerCatalog.cs` — definition source of truth; subscribes `Changed`
- `Quasar/Services/AgentRegistry.cs` — agent lookup, `SendCommand(AndWait)Async`
- [`Quasar/Services/DedicatedServerRuntimePreparer.cs`](DedicatedServerRuntimePreparer.cs.md) — pre-launch file preparation
- [`Quasar/Services/ManagedDedicatedServerRuntimeResolver.cs`](ManagedDedicatedServerRuntimeResolver.cs.md) — executable / DS64 path resolution
- [`Quasar/Services/ManagedRuntimeWarmupService.cs`](ManagedRuntimeWarmupService.cs.md) — launch readiness gate
- [`Quasar/Services/PluginSdk/PluginLogStream.cs`](PluginSdk/PluginLogStream.cs.md) — stdout parsing/append
- `Quasar/Services/AtomicFileWriter.cs` — persisted state writes
- `Quasar/Services/WebServiceOptions.cs` — agent env-var values, `PreserveManagedServersOnShutdown`, `DisableServerHealthMonitoring`, `AvoidSimultaneousScheduledRestarts`
- `Quasar/Models/DedicatedServerDefinition.cs`, process/health/goal enums
- [`Quasar/Models/CpuAffinitySpec.cs`](../Models/CpuAffinitySpec.cs.md) — parse / Windows mask for per-server CPU affinity
- `Magnetar.Protocol.Runtime` (`MagnetarPaths`), `Magnetar.Protocol.Transport` (`ServerCommandEnvelope`, `ServerCommandType`)
- BCL `System.Diagnostics.Process`; Linux `renice`, `taskset`

## Notes

`_preserveManagedServersOnShutdown` defaults from `WebServiceOptions.PreserveManagedServersOnShutdown`: managed servers are left running on a normal Quasar stop (they are detached via Magnetar `-daemon`/setsid) and reconnect when Quasar returns; the persisted PID snapshot is how the next worker re-adopts them. `StartInProgress` closes a TOCTOU race where two concurrent reconciles could both launch a process and collide on the port. Process priority applies in two phases (`StartupProcessPriority` at launch, `ReadyProcessPriority` once healthy/agent-online); on Linux via `renice -n {nice} -p {pid}`. Linux native-library search paths come from `ResolvedDedicatedServerRuntime.NativeLibrarySearchPaths` and are prepended to the inherited `LD_LIBRARY_PATH`, mainly to expose SteamCMD's `linux64/steamclient.so` runtime to Steam GameServer init on headless hosts. Managed server launches are not allowed until the warmup service has prepared managed SteamCMD and Dedicated Server prerequisites. Per-server launch diagnostics are UI-owned and persisted on the server definition, not in appsettings. Simulation-progress health re-baselines (skips judgement) during active world saves. State persists with a 150 ms debounce on every change and synchronously on shutdown/drain. DS log retention is enforced on Quasar-captured stdout/stderr archives, not Magnetar-internal files.
