# Quasar/Services/QuasarShutdownService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`QuasarShutdownService` orchestrates stopping all managed Magnetar servers and recycling the Quasar worker — with or without leaving those servers running. It distinguishes three operations: stop every running server (leaving Quasar up), full shutdown (stop servers then stop the host), and worker restart (leave servers running, drain the supervisor, and stop the worker so a launcher respawns it). An `IProgress<string>` parameter streams status messages to the UI during each sequence.

## Structure

Namespace: `Quasar.Services`

**`QuasarShutdownService`** — sealed class. ctor injects `IHostApplicationLifetime` and `DedicatedServerSupervisor`.

| Member | Description |
|---|---|
| `StopAllServersAsync(progress?, ct, bool setGoalStateOff = false)` | Selects servers in Starting/Running/Restarting/Stopping states and stops each sequentially (best-effort, exceptions swallowed). Quasar keeps running; the worker is not restarted. When `setGoalStateOff` is true, each server's goal state is set to Off (via `supervisor.SetGoalStateAsync` with `reconcile:false`) **before** stopping it, so the reconcile loop treats the shutdown as intentional and won't auto-restart — used by the admin "Shut down all servers" action where Quasar stays up. Left false for full Quasar shutdown so servers resume on next worker boot per their goal state. |
| `ShutdownAsync(progress?, ct)` | Calls `StopAllServersAsync`, then `IHostApplicationLifetime.StopApplication()`. Used by the launcher-driven full shutdown (drain endpoint / POSIX signals). |
| `RestartWorker(progress?)` | Leaves managed servers running: calls `_supervisor.BeginLauncherDrain()` (marks preserve-on-stop and persists the runtime PID snapshot) then `StopApplication()` so the Bootstrap launcher respawns the worker and re-adopts servers by PID. Standalone (no launcher) this simply stops the worker. |

## Dependencies

- [`Quasar/Services/DedicatedServerSupervisor.cs`](DedicatedServerSupervisor.cs.md) — `GetSnapshots()`, `StopServerAsync()`, `BeginLauncherDrain()`
- `Quasar/Models/` — `DedicatedServerProcessState` (via snapshot)
- `Microsoft.Extensions.Hosting.IHostApplicationLifetime`

## Notes

Stops are sequential, not parallel, to avoid overwhelming server processes; per-server exceptions are caught so remaining servers still get their stop signal. `RestartWorker` is the path that keeps Magnetar servers alive across a Quasar worker turnover — the running servers are detached via `-daemon`, and `BeginLauncherDrain` persists their PIDs so the respawned worker can re-adopt them.
