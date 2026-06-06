# Quasar/Services/Backup/QuasarBackupSettingsService.cs

**Module:** Quasar.Services.Backup  **Kind:** class  **Tier:** 2

## Summary
Singleton store for the automatic-backup schedule. Persists to `backup-settings.json` (`MagnetarPaths.GetQuasarBackupSettingsPath()`) in the Quasar data directory and picks up external edits via a debounced (250 ms) `FileSystemWatcher`, mirroring `BrandingService`.

## Structure
Namespace: `Quasar.Services.Backup`

`public sealed class QuasarBackupSettingsService` : `IDisposable`

`JsonSerializerOptions`: Web defaults, ignore-null, indented, `JsonStringEnumConverter`.

| Member | Description |
|---|---|
| `event Action? Changed` | Fired after any settings mutation or disk reload. |
| `GetSettings()` | Returns a deep `Clone` of `QuasarBackupSettings` safe for UI draft editing. |
| `SaveAsync(QuasarBackupSettings, CancellationToken)` | Normalizes (`QuasarBackupSettings.Normalize`), preserves the scheduler's own `LastBackupUtc` across UI saves, persists, fires `Changed`. |
| `UpdateLastBackupAsync(DateTimeOffset, CancellationToken)` | Records the last automatic backup time. |

Private `PersistAsync` uses `AtomicFileWriter.WriteTextAsync`. `StartWatching` / `ScheduleReload` (debounce via `CancellationTokenSource`) / `ReloadFromDisk` fire `Changed` only when the serialized snapshot differs. `Dispose` tears down the watcher and debounce.

## Dependencies
- [`Magnetar.Protocol/Runtime/MagnetarPaths.cs`](../../../Magnetar.Protocol/Runtime/MagnetarPaths.cs.md)
- [`Quasar/Models/QuasarBackupSettings.cs`](../../Models/QuasarBackupSettings.cs.md)
- [`Quasar/Services/AtomicFileWriter.cs`](../AtomicFileWriter.cs.md)
- External: System.Text.Json

## Notes
Thread-safe via `lock(_sync)`. Reload is debounced; writes are atomic. The snapshot comparison suppresses spurious `Changed` events when Quasar writes the file itself.
