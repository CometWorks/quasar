# Quasar/Services/Backup/AutomaticBackupService.cs

**Module:** Quasar.Services.Backup  **Kind:** class  **Tier:** 2

## Summary
Background scheduler that writes automatic Quasar config, server, and world backups into the Backups directory according to separate `QuasarBackupSettings` rules and prunes them to each rule's retention count. Modeled on `PluginCatalogRefreshService`'s `PeriodicTimer` pattern.

## Structure
Namespace: `Quasar.Services.Backup`

`public sealed class AutomaticBackupService` : `BackgroundService`

`StartupDelay` 10 s, `TickInterval` 1 min. Registered in `Program.cs` as a singleton plus `AddHostedService`. Constructor deps: `QuasarBackupService`, `QuasarBackupSettingsService`, `DedicatedServerCatalog`, `ILogger`.

| Member | Description |
|---|---|
| `ExecuteAsync` | Initial delay, then a `PeriodicTimer` loop calling `RunDueBackupAsync`. |
| `RunEnabledBackupsNowAsync(CancellationToken)` | Performs every enabled automatic rule immediately regardless of schedule; returns the number of ZIPs created. |

Private `RunDueBackupAsync` checks `Configuration`, `Server`, and `World` rules independently. `PerformBackupAsync` writes config backups once, and server/world backups once per configured server. Retention pruning is per kind; server/world pruning is also per server. `_settingsService.UpdateLastBackupAsync(kind, timestamp)` records each rule's last run. Static `IsDue` / `ComputeNextDueLocal` compute the next due time from per-rule `LastBackupUtc` plus frequency (Hourly/Daily/Weekly + time-of-day/day-of-week). The first run after enabling a rule happens at the next tick.

## Dependencies
- [`Quasar/Services/Backup/QuasarBackupService.cs`](QuasarBackupService.cs.md)
- [`Quasar/Services/Backup/QuasarBackupSettingsService.cs`](QuasarBackupSettingsService.cs.md)
- [`Quasar/Services/DedicatedServerCatalog.cs`](../DedicatedServerCatalog.cs.md)
- [`Quasar/Models/QuasarBackupSettings.cs`](../../Models/QuasarBackupSettings.cs.md)

## Notes
`OperationCanceledException` on shutdown is swallowed. Scheduled failures are logged as warnings and the loop continues.
