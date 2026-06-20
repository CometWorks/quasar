# Quasar/Services/Backup/QuasarBackupSettingsService.cs

**Module:** Quasar.Services.Backup  **Kind:** class  **Tier:** 2

## Summary
Singleton store for automatic-backup rules and the stored-backup folder setting. Persists schedule rules to `backup-settings.json` (`MagnetarPaths.GetQuasarBackupSettingsPath()`) in the Quasar data directory and picks up external schedule edits via a debounced (250 ms) `FileSystemWatcher`, mirroring `BrandingService`. It also patches `Quasar:BackupDirectory` in the data-directory `appsettings.json` for the Backup page and applies the resolved path to the live `WebServiceOptions`.

## Structure
Namespace: `Quasar.Services.Backup`

`public sealed class QuasarBackupSettingsService` : `IDisposable`
`public sealed class QuasarBackupDirectorySettings`

`JsonSerializerOptions`: Web defaults, ignore-null, indented, `JsonStringEnumConverter`.

| Member | Description |
|---|---|
| `event Action? Changed` | Fired after any settings mutation or disk reload. |
| `event Action? BackupDirectoryChanged` | Fired after the stored-backup folder is saved through the web UI. |
| `AppSettingsPath` | Data-directory `appsettings.json` path patched for `Quasar:BackupDirectory`. |
| `GetSettings()` | Returns a deep `Clone` of `QuasarBackupSettings` safe for UI draft editing. |
| `SaveAsync(QuasarBackupSettings, CancellationToken)` | Normalizes (`QuasarBackupSettings.Normalize`), preserves each rule's scheduler-owned `LastBackupUtc` across UI saves, persists, fires `Changed`. |
| `UpdateLastBackupAsync(QuasarBackupKind, DateTimeOffset, CancellationToken)` | Records the last automatic backup time for one backup kind/rule. |
| `GetBackupDirectorySettings()` | Reads the configured appsettings value, current resolved directory, environment override state, and settings file path for the Backup page editor. |
| `SaveBackupDirectoryAsync(string?, CancellationToken)` | Validates/creates the resolved folder, writes `Quasar:BackupDirectory` to data-directory `appsettings.json`, updates `WebServiceOptions.BackupDirectory`, and fires `BackupDirectoryChanged`; refused while `QUASAR_BACKUP_DIR` is set. |

Private `PersistAsync` uses `AtomicFileWriter.WriteTextAsync` for `backup-settings.json`. `ReadConfiguredBackupDirectory`, `ReadAppSettingsAsync`, and `GetOrCreateObject` support the appsettings patch path guarded by `_appSettingsGate`. `StartWatching` / `ScheduleReload` (debounce via `CancellationTokenSource`) / `ReloadFromDisk` fire `Changed` only when the serialized schedule snapshot differs. `Dispose` tears down the watcher and debounce.

## Dependencies
- [`Magnetar.Protocol/Runtime/MagnetarPaths.cs`](../../../Magnetar.Protocol/Runtime/MagnetarPaths.cs.md)
- [`Quasar/Models/QuasarBackupSettings.cs`](../../Models/QuasarBackupSettings.cs.md)
- [`Quasar/Services/AtomicFileWriter.cs`](../AtomicFileWriter.cs.md)
- [`Quasar/Services/WebServiceOptions.cs`](../WebServiceOptions.cs.md)
- External: System.Text.Json

## Notes
Schedule state is thread-safe via `lock(_sync)`; appsettings writes are serialized by `_appSettingsGate`. Reload is debounced; writes are atomic. The schedule snapshot comparison suppresses spurious `Changed` events when Quasar writes the file itself. `QUASAR_BACKUP_DIR` remains highest priority and disables UI edits to avoid making the live process disagree with restart-time configuration.
