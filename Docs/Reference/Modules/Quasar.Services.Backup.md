# Quasar.Services.Backup — Configuration Backup & Restore

*Module `Quasar.Services.Backup` — 5 files.* See the [handbook TOC](../TOC.md) and the [file Index](../Index.md).

Backs up and restores Quasar configuration, server runtime state, and world-only data as versioned ZIPs. `QuasarBackupService` builds configuration archives (`data/` + `branding-assets/`), server archives (server definition plus non-cache DS/Magnetar app data), and world archives (world files excluding `Sandbox_config.sbc*`). It prefers the latest valid Space Engineers `Backup` snapshot when present so world backups can be taken while servers run, ensures the `Backups` directory exists on startup without replacing existing directories/symlinks, and publishes stored ZIPs atomically from same-directory `final.zip.tmp` files. `QuasarBackupSettingsService` persists the automatic configuration-backup schedule (`backup-settings.json`) with a debounced file watch, and `AutomaticBackupService` writes scheduled backups plus queued manual backup jobs into the `Backups` directory and prunes them to the retention count. `BackupCompatibility` and `BackupFormatMigrations` enforce semantic-version restore rules. Download endpoints and DI registration live in [Quasar.Host](Quasar.Host.md); the operator UI is `Backup.razor` in [Quasar.Components](Quasar.Components.md).

## Files

| File | Kind | Summary |
| --- | --- | --- |
| [Quasar/Services/Backup/AutomaticBackupService.cs](../files/Quasar/Services/Backup/AutomaticBackupService.cs.md) | class | `BackgroundService` scheduler and manual backup queue that writes configuration, server, and world backups into the `Backups` directory, serializes backup jobs behind a gate, reports queued-job completion, and prunes old automatic backups to the retention count. |
| [Quasar/Services/Backup/BackupCompatibility.cs](../files/Quasar/Services/Backup/BackupCompatibility.cs.md) | class (static) | Applies the semantic-versioning rules governing whether a backup may restore into the running Quasar: same major.minor is always allowed (patch may differ either way); an older major.minor is allowed only when a forward migration path exists; a newer major.minor is rejected (no cross-major.minor downgrade). Returns a `BackupCompatibilityResult` record struct. |
| [Quasar/Services/Backup/BackupFormatMigrations.cs](../files/Quasar/Services/Backup/BackupFormatMigrations.cs.md) | class (static) | Registry of forward upgrade steps (`BackupMigrationStep`) that migrate backup contents from one major.minor release to the next, with `CanMigrate` walking a contiguous chain from the backup version to the running version. The step list is empty today, so only same-major.minor restores are accepted until the first persisted-structure change ships. |
| [Quasar/Services/Backup/QuasarBackupService.cs](../files/Quasar/Services/Backup/QuasarBackupService.cs.md) | class | Builds and restores Quasar configuration, server, and world ZIP backups. Creates in-memory config archives for download, writes server archives without cache folders/world files, writes stored ZIPs through temporary files before atomic rename, lists/prunes/deletes them, dispatches restore by manifest kind after a version-compatibility check, uses latest SE world backup snapshots when available, and guards against path traversal/zip slip. |
| [Quasar/Services/Backup/QuasarBackupSettingsService.cs](../files/Quasar/Services/Backup/QuasarBackupSettingsService.cs.md) | class | Singleton store for the automatic-backup schedule (`QuasarBackupSettings`), persisting to `backup-settings.json` via `AtomicFileWriter` and hot-reloading on external edits through a debounced `FileSystemWatcher`, mirroring `BrandingService`. Preserves the scheduler's own `LastBackupUtc` bookkeeping across UI saves. |

## Depends on

- [Magnetar.Protocol](Magnetar.Protocol.md)
- [Quasar.Models](Quasar.Models.md)
- [Quasar.Services.Core](Quasar.Services.Core.md)
