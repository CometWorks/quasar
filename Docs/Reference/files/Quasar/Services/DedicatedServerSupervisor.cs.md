# Quasar/Services/DedicatedServerSupervisor.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`DedicatedServerSupervisor` is the heart of Quasar's process management. It is an `IHostedService` that maintains in-memory `ManagedServerState` for every configured dedicated server, runs a 2-second reconcile loop that starts/stops/restarts processes to match goal state, evaluates server health (agent heartbeat, simulation frame progress, uptime thresholds), persists runtime state across Quasar worker restarts and **adopts surviving detached processes by PID on startup**, and coordinates graceful stop (save + stop commands to the agent before kill) plus scheduled and maximum-uptime restarts.

## Structure

Namespace: `Quasar.Services`

**`DedicatedServerSupervisor`** — sealed class implementing `IHostedService`, `IDisposable`.

| Member | Description |
|---|---|
| `event Action? Changed` | Raised after any state change; `NotifyChanged` also schedules a debounced state persist. |
| `StartAsync(ct)` | Syncs definitions from catalog, restores persisted state (adopting live PIDs), subscribes to catalog `Changed`, launches the reconcile loop, persists. |
| `StopAsync(ct)` | If `_preserveManagedServersOnShutdown` (default), only persists a snapshot and leaves servers running; otherwise stops all running servers then persists. |
| `GetSnapshots()` | Cloned `DedicatedServerRuntimeSnapshot` per server, merged with current agent connectivity. |
| `SetGoalStateAsync(...)` | Delegates to catalog then reconciles immediately. |
| `StartServerAsync(...)` | Guarded by `StartInProgress`; resolves runtime, prepares files, spawns the process with full env vars, applies startup priority, starts stdout/stderr pumps. |
| `StopServerAsync(uniqueName, forceAfter?, ct)` | Sends `SaveWorld` + `StopServer` to the agent, waits for exit, kills the process tree if the grace window expires. |
| `RestartServerAsync(...)` | Sets goal On + AutoStart, stops, starts. |
| `BeginLauncherDrain()` | Sets `_preserveManagedServersOnShutdown = true` and persists synchronously — called before a worker-only restart so the next worker can re-adopt. |
| `Dispose()` | Cancels the persist-debounce CTS and the shutdown CTS. |

**`ReconcileAsync`** — per server: liveness vs goal state → Start/Stop/Restart; unhealthy auto-restart (`AutoRestartOnUnhealthy`, throttled by `CanScheduleHealthRestart`); maximum-uptime restart; daily scheduled restart; both planned restarts honour `AvoidSimultaneousScheduledRestarts` via `CanRunPlannedRestart`. Also promotes Starting/Restarting → Running once the agent reports `IsRunning`, and applies `ReadyProcessPriority` once healthy.

**`EvaluateHealth` / `EvaluateSimulationProgress`** — agent connectivity, heartbeat staleness, simulation frame-progress score (frames/sec normalised to 60 Hz), uptime warn/recycle thresholds. Honours `DisableServerHealthMonitoring` and per-definition `EnableHealthMonitoring`. Agent-attach grace counts from `AgentWatchSinceUtc`.

**`RestorePersistedRuntimeState` / `TryAdoptProcess`** — on startup, `Process.GetProcessById` re-adopts still-running DS processes from a prior worker, re-attaches the `Exited` handler, and resets `AgentWatchSinceUtc` to "now" so the agent gets a fresh reconnect grace; processes no longer alive are marked Stopped.

**`PumpStandardOutputAsync` / `PumpStandardErrorAsync`** — append timestamped lines to per-server log files. Plugin-SDK JSON lines (`TryParseSinkLine`) are **skipped** here because they now arrive via the agent network relay (`AgentSocketHandler`); only non-plugin output is wrapped as Magnetar-source `PluginLogEntry`. stderr lines log at Error.

Private nested types: `ManagedServerState` (full mutable per-server state incl. `Process`, `StartInProgress`, `AgentWatchSinceUtc`, simulation/priority/scheduled-restart tracking); `PersistedSupervisorState` / `PersistedManagedServerState` (JSON-serialised subset incl. `ProcessId`); `ReconcileAction` enum; `ServerHealthAssessment` readonly record struct.

## Dependencies

- `Quasar/Services/DedicatedServerCatalog.cs` — definition source of truth; subscribes `Changed`
- `Quasar/Services/AgentRegistry.cs` — agent lookup, `SendCommand(AndWait)Async`
- [`Quasar/Services/DedicatedServerRuntimePreparer.cs`](DedicatedServerRuntimePreparer.cs.md) — pre-launch file preparation
- [`Quasar/Services/ManagedDedicatedServerRuntimeResolver.cs`](ManagedDedicatedServerRuntimeResolver.cs.md) — executable / DS64 path resolution
- [`Quasar/Services/PluginSdk/PluginLogStream.cs`](PluginSdk/PluginLogStream.cs.md) — stdout parsing/append
- `Quasar/Services/AtomicFileWriter.cs` — persisted state writes
- `Quasar/Services/WebServiceOptions.cs` — agent env-var values, `PreserveManagedServersOnShutdown`, `DisableServerHealthMonitoring`, `AvoidSimultaneousScheduledRestarts`
- `Quasar/Models/DedicatedServerDefinition.cs`, process/health/goal enums
- `Magnetar.Protocol.Runtime` (`MagnetarPaths`), `Magnetar.Protocol.Transport` (`ServerCommandEnvelope`, `ServerCommandType`)
- BCL `System.Diagnostics.Process`; Linux `renice`

## Notes

`_preserveManagedServersOnShutdown` defaults from `WebServiceOptions.PreserveManagedServersOnShutdown`: managed servers are left running on a normal Quasar stop (they are detached via Magnetar `-daemon`/setsid) and reconnect when Quasar returns; the persisted PID snapshot is how the next worker re-adopts them. `StartInProgress` closes a TOCTOU race where two concurrent reconciles could both launch a process and collide on the port. Process priority applies in two phases (`StartupProcessPriority` at launch, `ReadyProcessPriority` once healthy/agent-online); on Linux via `renice -n {nice} -p {pid}`. Simulation-progress health re-baselines (skips judgement) during active world saves. State persists with a 150 ms debounce on every change and synchronously on shutdown/drain.
