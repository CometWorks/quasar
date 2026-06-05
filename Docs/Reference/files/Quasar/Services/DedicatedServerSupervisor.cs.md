# Quasar/Services/DedicatedServerSupervisor.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`DedicatedServerSupervisor` is the heart of Quasar's process management. It is an `IHostedService` that maintains in-memory `ManagedServerState` for every configured dedicated server, runs a 2-second reconcile loop that starts/stops/restarts processes to match goal state, evaluates server health (agent heartbeat, simulation frame progress, uptime thresholds), persists supervisor state across Quasar restarts with process adoption, and coordinates graceful stop (save + stop commands to agent before kill) and scheduled/maximum-uptime restarts.

## Structure

Namespace: `Quasar.Services`

**`DedicatedServerSupervisor`** — sealed class implementing `IHostedService`, `IDisposable`.

| Member | Description |
|---|---|
| `event Action? Changed` | Raised after any state change; debounced to also persist state. |
| `StartAsync(ct)` | Syncs definitions from catalog, restores persisted state, subscribes to `Changed` catalog event, starts reconcile loop. |
| `StopAsync(ct)` | Stops all running servers (unless `_preserveManagedServersOnShutdown`), persists state. |
| `GetSnapshots()` | Returns cloned `DedicatedServerRuntimeSnapshot` list for all managed servers. |
| `SetGoalStateAsync(uniqueName, goalState, ct)` | Delegates to catalog then immediately reconciles. |
| `StartServerAsync(uniqueName, ct)` | Resolves runtime, prepares files, spawns process with full env vars, starts stdout/stderr pumps. |
| `StopServerAsync(uniqueName, forceAfter?, ct)` | Sends `SaveWorld` + `StopServer` to agent, waits for exit, kills process tree if grace period expires. |
| `RestartServerAsync(uniqueName, ct)` | Sets goal-On, stops, starts. |
| `BeginLauncherDrain()` | Marks `_preserveManagedServersOnShutdown`; persists state immediately (used by Quasar.Bootstrap for hot-handoff). |
| `Dispose()` | Cancels persist-debounce CTS and shutdown CTS. |

**`ReconcileAsync`** — evaluates per-server: process liveness vs goal state → Start/Stop/Restart actions; unhealthy auto-restart if `AutoRestartOnUnhealthy`; maximum-uptime restart; scheduled daily restart with simultaneous-restart avoidance; process-priority transitions on startup→ready.

**`EvaluateHealth` / `EvaluateSimulationProgress`** — checks agent connectivity, heartbeat staleness, simulation frame progress rate (frames/second normalised to 60 Hz target), uptime warn/recycle thresholds.

**`RestorePersistedRuntimeState`** — on startup tries `Process.GetProcessById` to adopt still-running DS processes from a previous Quasar worker; re-attaches `Exited` handlers.

**`PumpStandardOutput/Error`** — appends to per-server log files; parses structured JSON plugin-log lines via `PluginLogStream.TryParseSinkLine`; wraps raw stdout lines as Magnetar-source entries.

Private nested types:
- `ManagedServerState` — full per-server mutable state: `Process`, health fields, simulation progress tracking, scheduled-restart tracking, priority tracking.
- `PersistedSupervisorState` / `PersistedManagedServerState` — JSON-serialisable subset for cross-restart state.
- `ReconcileAction` — enum `{Start, Stop, Restart}`.
- `ServerHealthAssessment` — readonly record struct returned by `EvaluateHealth`.

## Dependencies

- [`Quasar/Services/DedicatedServerCatalog.cs`](DedicatedServerCatalog.cs.md) — definition source of truth; subscribes `Changed`
- [`Quasar/Services/AgentRegistry.cs`](AgentRegistry.cs.md) — agent lookup, command dispatch
- [`Quasar/Services/DedicatedServerRuntimePreparer.cs`](DedicatedServerRuntimePreparer.cs.md) — pre-launch file preparation
- [`Quasar/Services/ManagedDedicatedServerRuntimeResolver.cs`](ManagedDedicatedServerRuntimeResolver.cs.md) — executable/DS64 path resolution
- [`Quasar/Services/PluginSdk/PluginLogStream.cs`](PluginSdk/PluginLogStream.cs.md) — stdout log parsing and append
- `Quasar/Services/AtomicFileWriter.cs` — supervisor state persistence
- [`Quasar/Services/WebServiceOptions.cs`](WebServiceOptions.cs.md) — agent env var values, `DisableServerHealthMonitoring`, `AvoidSimultaneousScheduledRestarts`
- [`Quasar/Models/DedicatedServerDefinition.cs`](../Models/DedicatedServerDefinition.cs.md) — definition model
- `Magnetar.Protocol.Runtime` — `MagnetarPaths`, state enums
- `Magnetar.Protocol.Transport` — `ServerCommandEnvelope`, `ServerCommandType`
- BCL `System.Diagnostics.Process`, `renice` (Linux)

## Notes

The `StartInProgress` flag on `ManagedServerState` closes a TOCTOU race: two concurrent reconcile iterations (periodic loop + catalog-change triggered) could both pass the `IsProcessActive` check and launch duplicate processes on the same port. Process priority is applied in two phases: `StartupProcessPriority` at launch, `ReadyProcessPriority` once the agent reports `IsRunning`. Simulation-progress health skips evaluation during active world saves (`IsSaveInProgress`). State is persisted with a 150 ms debounce on every `Changed` notification and synchronously on shutdown. On Linux, process priority adjustment calls `renice -n {nice} -p {pid}`.
