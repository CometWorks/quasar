# Quasar/Services/Backup/AutomaticBackupService.cs

**Module:** Quasar.Services.Backup  **Kind:** class  **Tier:** 2

## Summary
Background scheduler and manual backup queue that writes Quasar config, server, and world backups into the configured backup directory. Scheduled runs follow separate `QuasarBackupSettings` rules and prune to each rule's retention count; manual UI starts are queued so Blazor page navigation is not blocked by ZIP creation.

## Structure
Namespace: `Quasar.Services.Backup`

`public enum QueuedBackupJobKind` — queued manual job type (`EnabledRules`, `Server`, `World`).
`public sealed record QueuedBackupJobResult(Guid Id, QueuedBackupJobKind Kind, string? ServerUniqueName, bool Success, int CreatedCount, string Message, Exception? Exception)` — completion payload raised for UI feedback.
`public sealed class AutomaticBackupService` : `BackgroundService`

`StartupDelay` 10 s, `TickInterval` 1 min. Registered in `Program.cs` as a singleton plus `AddHostedService`. Constructor deps: `QuasarBackupService`, `QuasarBackupSettingsService`, `DedicatedServerCatalog`, `ILogger`. Internally owns an unbounded `Channel<QueuedBackupJob>` with one reader plus a `SemaphoreSlim` gate so scheduled and queued backup jobs do not run at the same time.

| Member | Description |
|---|---|
| `ExecuteAsync` | Initial delay, then a `PeriodicTimer` loop calling `RunDueBackupAsync`. |
| `QueueEnabledBackupsNow()` | Enqueues an immediate run of all enabled automatic-backup rules and returns a job ID. |
| `QueueServerBackup(string uniqueName)` | Enqueues one manual server backup and returns a job ID. |
| `QueueWorldBackup(string uniqueName)` | Enqueues one manual world backup and returns a job ID. |
| `RunEnabledBackupsNowAsync(CancellationToken)` | Performs every enabled automatic rule immediately regardless of schedule under the backup gate; returns the number of ZIPs created. |

Private `RunDueBackupAsync` checks `Configuration`, `Server`, and `World` rules independently. `RunQueueAsync` drains manual queued jobs and reports success/failure through `QueuedBackupCompleted`. `PerformBackupAsync` writes config backups once, and server/world backups once per configured server. Retention pruning is per kind; server/world pruning is also per server. `_settingsService.UpdateLastBackupAsync(kind, timestamp)` records each rule's last run. Static `IsDue` / `ComputeNextDueLocal` compute the next due time from per-rule `LastBackupUtc` plus frequency (Hourly/Daily/Weekly + time-of-day/day-of-week). The first run after enabling a rule happens at the next tick.

## Dependencies
- [`Quasar/Services/Backup/QuasarBackupService.cs`](QuasarBackupService.cs.md)
- [`Quasar/Services/Backup/QuasarBackupSettingsService.cs`](QuasarBackupSettingsService.cs.md)
- [`Quasar/Services/DedicatedServerCatalog.cs`](../DedicatedServerCatalog.cs.md)
- [`Quasar/Models/QuasarBackupSettings.cs`](../../Models/QuasarBackupSettings.cs.md)

## Notes
`OperationCanceledException` on shutdown is swallowed. Scheduled and queued failures are logged as warnings and the loops continue. Startup calls `QuasarBackupService.CleanupIncompleteBackupFiles()` before processing queued or scheduled work, which creates the configured backup directory when missing and preserves existing directories/symlinks.
