# Quasar/Services/QuasarShutdownService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`QuasarShutdownService` orchestrates a graceful shutdown of all running dedicated servers before stopping the Quasar host process. It iterates servers in Starting/Running/Restarting/Stopping states, stops each one individually (best-effort, exceptions swallowed), then calls `IHostApplicationLifetime.StopApplication()`. An `IProgress<string>` parameter lets callers stream status messages to the UI during the shutdown sequence.

## Structure

Namespace: `Quasar.Services`

**`QuasarShutdownService`** — sealed class.

| Member | Description |
|---|---|
| `ShutdownAsync(progress, ct)` | Queries running servers from `DedicatedServerSupervisor`, stops each sequentially (best-effort), then requests host stop via `IHostApplicationLifetime.StopApplication()`. |

Constructor injects `IHostApplicationLifetime` and `DedicatedServerSupervisor`.

## Dependencies

- [`Quasar/Services/DedicatedServerSupervisor.cs`](DedicatedServerSupervisor.cs.md) — `GetSnapshots()`, `StopServerAsync()`
- `Quasar/Models/DedicatedServerProcessState` enum (via snapshot)
- `Microsoft.Extensions.Hosting.IHostApplicationLifetime`

## Notes

Stop operations are sequential, not parallel, to avoid overwhelming server processes during shutdown. Exceptions from individual `StopServerAsync` calls are caught and swallowed so that remaining servers still get their stop signal.
