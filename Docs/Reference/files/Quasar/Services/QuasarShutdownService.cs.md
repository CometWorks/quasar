# Quasar/Services/QuasarShutdownService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`QuasarShutdownService` orchestrates stopping all managed Magnetar servers and stopping or recycling the Quasar worker with clear server-preservation semantics. It distinguishes four operations: stop every running server while Quasar stays up, full shutdown that stops servers then stops the host, worker restart that leaves servers running for Bootstrap to re-adopt, and Quasar shutdown that stops the worker/launcher while preserving running servers.

## Structure

Namespace: `Quasar.Services`

**`QuasarShutdownService`** — sealed class. ctor injects `IHostApplicationLifetime`, `DedicatedServerSupervisor`, `WebServiceOptions`, and `ILogger<QuasarShutdownService>`.

| Member | Description |
|---|---|
| `StopAllServersAsync(progress?, ct, bool setGoalStateOff = false)` | Selects servers in Starting/Running/Restarting/Stopping states and stops each sequentially (best-effort, exceptions swallowed). Quasar keeps running; the worker is not restarted. When `setGoalStateOff` is true, each server's goal state is set to Off (via `supervisor.SetGoalStateAsync` with `reconcile:false`) **before** stopping it, so the reconcile loop treats the shutdown as intentional and won't auto-restart — used by the admin "Shut down all servers" action where Quasar stays up. Left false for full Quasar shutdown so servers resume on next worker boot per their goal state. |
| `ShutdownAsync(progress?, ct)` | Calls `StopAllServersAsync`, then `IHostApplicationLifetime.StopApplication()`. Used by the launcher-driven full shutdown (drain endpoint / POSIX signals). |
| `RestartWorker(progress?)` | Leaves managed servers running: calls `_supervisor.BeginLauncherDrain()` (marks preserve-on-stop and persists the runtime PID snapshot) then `StopApplication()` so the Bootstrap launcher respawns the worker and re-adopts servers by PID. Standalone (no launcher) this simply stops the worker. |
| `ShutdownQuasarPreservingServers(progress?)` | Leaves managed servers running: calls `_supervisor.BeginLauncherDrain()`, writes a Bootstrap `launcher-shutdown-request` file when launcher-managed, then stops the worker. |
| `RequestLauncherShutdown()` | Writes the timestamped request file in the Quasar data directory so Bootstrap exits cleanly instead of respawning the worker. |

## Dependencies

- [`Quasar/Services/DedicatedServerSupervisor.cs`](DedicatedServerSupervisor.cs.md) — `GetSnapshots()`, `StopServerAsync()`, `BeginLauncherDrain()`
- [`Quasar/Services/WebServiceOptions.cs`](WebServiceOptions.cs.md) — launcher token indicates when a Bootstrap shutdown request should be written
- `Quasar/Models/` — `DedicatedServerProcessState` (via snapshot)
- `Microsoft.Extensions.Hosting.IHostApplicationLifetime`

## Notes

Stops are sequential, not parallel, to avoid overwhelming server processes; per-server exceptions are caught so remaining servers still get their stop signal. `RestartWorker` and `ShutdownQuasarPreservingServers` both call `BeginLauncherDrain`, which keeps Magnetar servers alive, detaches them via `-daemon`, and persists their PIDs for later re-adoption.
