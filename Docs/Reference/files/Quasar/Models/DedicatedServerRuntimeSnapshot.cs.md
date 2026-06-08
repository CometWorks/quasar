# Quasar/Models/DedicatedServerRuntimeSnapshot.cs

**Module:** Quasar.Models  **Kind:** class  **Tier:** 1

## Summary
Immutable-by-convention snapshot of a server's live runtime state as maintained by `DedicatedServerSupervisor`. Combines process-lifecycle state, health classification, simulation-performance metrics, agent connectivity, log paths, and captured mod-download failures into a single transferable object used by the dashboard and API.

## Structure
Namespace: `Quasar.Models`  
`public sealed class DedicatedServerRuntimeSnapshot` — no base class, no interfaces.

| Member | Description |
|---|---|
| `UniqueName` | Identifies which server this snapshot belongs to. |
| `GoalState` | Operator's desired on/off intent at the time of the snapshot. |
| `State` | Current process lifecycle state (`DedicatedServerProcessState`). |
| `HealthState` | Current health classification (`DedicatedServerHealthState`). |
| `HealthSummary` | Human-readable explanation of the current health state. |
| `SimulationProgressScore` | Nullable float — ratio of sim frames advanced in the last window; null when unavailable. |
| `SimulationProgressWindowSeconds` | Observation window length used for the score above. |
| `SimulationFramesAdvanced` | Raw frame-advance count within the window. |
| `RestartAttempts` | Number of restart attempts since the last clean start. |
| `ProcessId` | OS PID of the server process; null when stopped. |
| `LastExitCode` | Exit code from the last process termination; null if not yet exited. |
| `LastMessage` | Supervisor status message (e.g. reason for last state change). |
| `AgentAttached` | Whether the Quasar.Agent WebSocket connection is currently active. |
| `AgentLastSeenUtc` | Timestamp of the last agent heartbeat. |
| `StartedAtUtc` | When the current process was started. |
| `StoppedAtUtc` | When the process last stopped. |
| `StandardOutputLogPath` | Path to the redirected stdout log file. |
| `StandardErrorLogPath` | Path to the redirected stderr log file. |
| `ModDownloadFailures` | Recent Magnetar/server output lines that look like Workshop mod download failures during startup/world initialization. |

## Dependencies
- [`Quasar/Models/DedicatedServerGoalState.cs`](DedicatedServerGoalState.cs.md)
- [`Quasar/Models/DedicatedServerProcessState.cs`](DedicatedServerProcessState.cs.md)
- [`Quasar/Models/DedicatedServerHealthState.cs`](DedicatedServerHealthState.cs.md)
