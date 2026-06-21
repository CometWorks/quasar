# Quasar/Services/QuasarShutdownService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`QuasarShutdownService` orchestrates stopping all managed Magnetar servers and stopping or recycling the Quasar worker with clear server-preservation semantics. It distinguishes four operations: stop every running server while Quasar stays up, full shutdown that stops servers then stops the host, worker restart that leaves servers running for Bootstrap to re-adopt, and Quasar shutdown that stops the installed systemd unit or launcher while preserving running servers.

## Structure

Namespace: `Quasar.Services`

**`QuasarShutdownService`** — sealed class. ctor injects `IHostApplicationLifetime`, `DedicatedServerSupervisor`, `WebServiceOptions`, and `ILogger<QuasarShutdownService>`.

| Member | Description |
|---|---|
| `StopAllServersAsync(progress?, ct, bool setGoalStateOff = false)` | Selects servers in Starting/Running/Restarting/Stopping states and stops each sequentially (best-effort, exceptions swallowed). Quasar keeps running; the worker is not restarted. When `setGoalStateOff` is true, each server's goal state is set to Off (via `supervisor.SetGoalStateAsync` with `reconcile:false`) **before** stopping it, so the reconcile loop treats the shutdown as intentional and won't auto-restart — used by the admin "Shut down all servers" action where Quasar stays up. Left false for full Quasar shutdown so servers resume on next worker boot per their goal state. |
| `ShutdownAsync(progress?, ct)` | Calls `StopAllServersAsync`, then `IHostApplicationLifetime.StopApplication()`. Used by the launcher-driven full shutdown (drain endpoint / POSIX signals). |
| `RestartWorker(progress?)` | Leaves managed servers running: calls `_supervisor.BeginLauncherDrain()` (marks preserve-on-stop and persists the runtime PID snapshot) then `StopApplication()` so the Bootstrap launcher respawns the worker and re-adopts servers by PID. Standalone (no launcher) this simply stops the worker. |
| `ShutdownQuasarPreservingServers(progress?)` | Leaves managed servers running: calls `_supervisor.BeginLauncherDrain()`, first attempts a Linux systemd stop for the current unit (`systemctl --user --no-block stop ...` or `systemctl --no-block stop ...`), then falls back to the Bootstrap `launcher-shutdown-request` file and local worker stop. |
| `TryRequestSystemdServiceStop()` | Linux-only best-effort systemd integration. Resolves the unit/scope from `QUASAR_SYSTEMD_SERVICE` + `QUASAR_SYSTEMD_SCOPE`, or from `/proc/self/cgroup` for older installs, then runs `systemctl --no-block stop <unit>`. Nonzero/failed starts log a warning and fall back. |
| `ResolveSystemdServiceTarget()` / `DetectSystemdServiceTargetFromCgroup()` | Helpers that normalize unit names (`.service`) and infer user vs system scope from systemd cgroup paths (`/user.slice/` or `/system.slice/`). |
| `RequestLauncherShutdown()` | Writes the timestamped request file in the Quasar data directory so Bootstrap exits cleanly instead of respawning the worker when systemd stop is unavailable. |

## Dependencies

- [`Quasar/Services/DedicatedServerSupervisor.cs`](DedicatedServerSupervisor.cs.md) — `GetSnapshots()`, `StopServerAsync()`, `BeginLauncherDrain()`
- [`Quasar/Services/WebServiceOptions.cs`](WebServiceOptions.cs.md) — launcher token indicates when a Bootstrap shutdown request should be written
- `Quasar/Models/` — `DedicatedServerProcessState` (via snapshot)
- `Microsoft.Extensions.Hosting.IHostApplicationLifetime`
- `System.Diagnostics.Process` — invokes `systemctl` for installed Linux systemd services

## Notes

Stops are sequential, not parallel, to avoid overwhelming server processes; per-server exceptions are caught so remaining servers still get their stop signal. `RestartWorker` and `ShutdownQuasarPreservingServers` both call `BeginLauncherDrain`, which keeps Magnetar servers alive, detaches them via `-daemon`, and persists their PIDs for later re-adoption. New Linux installs export the systemd unit metadata so the UI shutdown button stops `quasar.service`; older installs can still be detected from cgroups, and non-systemd/Windows/failed-systemctl paths retain the previous launcher-request fallback.
