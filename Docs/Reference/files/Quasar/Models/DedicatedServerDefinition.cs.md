# Quasar/Models/DedicatedServerDefinition.cs

**Module:** Quasar.Models  **Kind:** class  **Tier:** 1

## Summary
Persistent configuration record for a single managed Space Engineers dedicated server. Contains all fields needed to launch, supervise, health-monitor, rotate retained DS logs, emit optional launch diagnostics, select profiler mode, schedule restarts, and set the DS-advertised server/world names, as well as process-priority, CPU-affinity, and managed-runtime settings. Serialized to disk as part of the server catalog.

## Structure
Namespace: `Quasar.Models`  
`public sealed class DedicatedServerDefinition` — no base class, no interfaces.

| Member | Description |
|---|---|
| `DefaultDsLogFilesToKeep` / `MinimumDsLogFilesToKeep` / `MaximumDsLogFilesToKeep` | Retention bounds for Quasar-managed DS stdout/stderr logs (default 10, range 1-1000). |
| `DefaultMaxRestartAttempts` | Default consecutive crash-restart budget before the supervisor faults the server (3). |
| `DefaultAgentAttachRetryAttempts` / `DefaultAgentAttachRetryDelaySeconds` | Defaults for restarting a launched server when Quasar.Agent does not attach during startup grace (3 retries, 5 s delay). |
| `UniqueName` | Stable machine key for the server (filename-safe slug). |
| `DisplayName` | Human-readable label shown in the UI. |
| `InGameServerName` | Optional Space Engineers multiplayer-list server name written to `SpaceEngineers-Dedicated.cfg` as `ServerName`; when blank the preparer falls back to `DisplayName`, then `UniqueName`. |
| `InGameWorldName` | Optional Space Engineers world/save name written to `SpaceEngineers-Dedicated.cfg` as `WorldName` and to `LastSession.sbl` as `GameName`; when blank the preparer falls back to `UniqueName`. |
| `OriginalUniqueName` | Pre-rename value used during rename operations; `[JsonIgnore]`, never persisted. |
| `GoalState` | Desired on/off state (`DedicatedServerGoalState`). |
| `ExecutablePath` | Path to the SE dedicated server executable. |
| `WorkingDirectory` | Process working directory. |
| `ManagedRuntime` | Which Magnetar build / .NET runtime launches the server (`ManagedServerRuntime`, default `DotNet10`). Honored only on Windows, where both the .NET 10 (Interim) and .NET Framework 4.8 (Legacy) builds ship; the resolver forces `DotNet10` on non-Windows hosts. |
| `DedicatedServerAppDataPath` | SE server AppData path override. |
| `MagnetarAppDataPath` | Magnetar plugin AppData path. |
| `WorldPath` | Path to the world save directory. |
| `ConfigFilePath` | Path to the server config XML. |
| `ConfigProfileId` | Reference to a `QuasarConfigProfile` by ID. |
| `WorldTemplateId` | Reference to a `QuasarWorldTemplate` by ID. |
| `LaunchArguments` | Extra CLI arguments appended at launch. |
| `DisableImplicitMagnetarModLoad` | Controls Magnetar's implicit mod loading. Default false omits `-noimplicitmod`; true makes Quasar pass `-noimplicitmod` so Magnetar does not load `MagnetarMod`. |
| `LogLaunchEnvironment` | Per-server troubleshooting flag. When enabled from the server editor, the supervisor writes the final Magnetar executable path, arguments, working directory, and environment variables to Quasar logs on next start. Default false. |
| `AgentProfilerMode` | Per-server profiler mode (`SafeContinuous`, `DeepContinuous`, or `Off`), default `SafeContinuous`; forwarded to the agent at launch and editable live from Analytics. |
| `DsLogFilesToKeep` | Number of Quasar-managed DS log slots to retain, including the current `stdout.log` / `stderr.log` slot (default 10). |
| `ServerPort` | UDP game port (default 27016). |
| `ServerIP` | Bind IP (default "0.0.0.0"). |
| `AutoStart` | Whether Quasar starts this server on its own startup. |
| `EnableHealthMonitoring` | Enables simulation-progress health checks. |
| `AutoRestartOnUnhealthy` | Trigger restart when health degrades to Unhealthy. |
| `AgentStartupGraceSeconds` | Seconds to wait for agent attach before declaring unhealthy (default 180). |
| `AgentAttachRetryAttempts` | Consecutive attach-timeout retry cap before faulting (default 3, minimum 1). |
| `AgentAttachRetryDelaySeconds` | Delay between attach-timeout relaunch attempts (default 5 s). |
| `AgentHeartbeatTimeoutSeconds` | Agent heartbeat timeout (default 20 s). |
| `SimulationProgressWindowSeconds` | Rolling window for sim-progress score (default 30 s). |
| `MinimumSimulationProgressScore` | Floor sim-progress score before unhealthy (default 0.1). |
| `WarnAfterUptimeHours` | Hours before a warning health state is raised. |
| `RecycleAfterUptimeHours` | Hours before a scheduled graceful restart (0 = disabled). |
| `RestartOnCrash` | Whether to restart after an unexpected process exit. |
| `RestartDelaySeconds` | Pause before restart attempt (default 5 s). |
| `MaxRestartAttempts` | Consecutive restart attempt cap before faulting (default 3, minimum 1). |
| `DailyRestartTimeLocal` | Local time string for a daily scheduled restart (empty = disabled). |
| `MaximumUptime` | TimeSpan string for maximum uptime before forced restart (empty = disabled). |
| `AvoidSimultaneousScheduledRestarts` | Staggers restarts so multiple servers don't stop at once. |
| `StartupProcessPriority` | OS process priority while the server is starting up (default `Normal`). |
| `ReadyProcessPriority` | OS process priority once the server is running. |
| `CpuAffinity` | Canonical cpuset string (e.g. "0-7" or "0-7,16-23") pinning the server process to a fixed set of logical cores; empty = no affinity (all cores); when set must contain >=2 cores; applied locally by the supervisor each time the process starts (see `CpuAffinitySpec`). Default empty. |
| `UpdatedAtUtc` | Timestamp of the last configuration save. |
| `Clone()` | Shallow copy of all fields (including in-game names, `ManagedRuntime`, `AgentProfilerMode`, `CpuAffinity`, `DisableImplicitMagnetarModLoad`, `LogLaunchEnvironment`, and `DsLogFilesToKeep`, used before mutations). |

## Dependencies
- [`Quasar/Models/DedicatedServerGoalState.cs`](DedicatedServerGoalState.cs.md)
- `Quasar/Models/DedicatedServerProcessPriority.cs`
- [`Quasar/Models/ManagedServerRuntime.cs`](ManagedServerRuntime.cs.md)
- `Quasar/Models/CpuAffinitySpec.cs`

## Notes
`OriginalUniqueName` carries the pre-rename key across a rename transaction so the supervisor can clean up the old runtime entry; it must never be written to disk.
