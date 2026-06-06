# Quasar/Services/Backup/AutomaticBackupService.cs

**Module:** Quasar.Services.Backup  **Kind:** class  **Tier:** 2

## Summary
Background scheduler that writes automatic backups into the Backups directory according to `QuasarBackupSettings` and prunes them to the configured retention count. Modeled on `PluginCatalogRefreshService`'s `PeriodicTimer` pattern.

## Structure
Namespace: `Quasar.Services.Backup`

`public sealed class AutomaticBackupService` : `BackgroundService`

`StartupDelay` 10 s, `TickInterval` 1 min. Registered in `Program.cs` as a singleton plus `AddHostedService`. Constructor deps: `QuasarBackupService`, `QuasarBackupSettingsService`, `ILogger`.

| Member | Description |
|---|---|
| `ExecuteAsync` | Initial delay, then a `PeriodicTimer` loop calling `RunDueBackupAsync`. |
| `RunBackupNowAsync(CancellationToken)` | Performs a backup immediately regardless of schedule/enabled (used by the "Make a backup now" button). |

Private `RunDueBackupAsync` checks `settings.Enabled` and `IsDue`. `PerformBackupAsync` calls `_backupService.WriteBackupFileAsync(automatic: true)`, then `PruneAutomaticBackups(retentionCount)`, then `_settingsService.UpdateLastBackupAsync`. Static `IsDue` / `ComputeNextDueLocal` compute the next due time from `LastBackupUtc` plus frequency (Hourly/Daily/Weekly + time-of-day/day-of-week). The first run after enabling happens at the next tick.

## Dependencies
- [`Quasar/Services/Backup/QuasarBackupService.cs`](QuasarBackupService.cs.md)
- [`Quasar/Services/Backup/QuasarBackupSettingsService.cs`](QuasarBackupSettingsService.cs.md)
- [`Quasar/Models/QuasarBackupSettings.cs`](../../Models/QuasarBackupSettings.cs.md)

## Notes
`OperationCanceledException` on shutdown is swallowed. Scheduled failures are logged as warnings and the loop continues.
