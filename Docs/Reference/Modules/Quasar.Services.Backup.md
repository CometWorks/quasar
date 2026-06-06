# Quasar.Services.Backup — Configuration Backup & Restore

*Module `Quasar.Services.Backup` — 5 files.* See the [handbook TOC](../TOC.md) and the [file Index](../Index.md).

Backs up and restores Quasar's own configuration as a single versioned ZIP. `QuasarBackupService` builds the archive (a `quasar-backup.json` manifest plus a `data/` mirror of the singleton config files and per-entity server/profile/world-template definitions, and a `branding-assets/` copy of uploaded logo/favicon images) and restores it by merging files back over their on-disk paths — deliberately excluding game servers, worlds, plugin configurations, runtime state, logs and history. `QuasarBackupSettingsService` persists the automatic-backup schedule (`backup-settings.json`) with a debounced file watch, and `AutomaticBackupService` is the `BackgroundService` that writes scheduled backups into the `Backups` directory and prunes them to the retention count. `BackupCompatibility` and `BackupFormatMigrations` enforce the semantic-versioning rules that decide whether a given backup may be restored into the running build. Download endpoints and DI registration live in [Quasar.Host](Quasar.Host.md); the operator UI is `Backup.razor` in [Quasar.Components](Quasar.Components.md).

## Files

| File | Kind | Summary |
| --- | --- | --- |
| [Quasar/Services/Backup/AutomaticBackupService.cs](../files/Quasar/Services/Backup/AutomaticBackupService.cs.md) | class | `BackgroundService` scheduler that writes automatic configuration backups into the `Backups` directory according to `QuasarBackupSettings` and prunes old ones to the retention count. Modeled on the `PluginCatalogRefreshService` PeriodicTimer pattern (10 s startup delay, 1 min tick). Also exposes `RunBackupNowAsync` for the "Make a backup now" action, which runs immediately regardless of the schedule. |
| [Quasar/Services/Backup/BackupCompatibility.cs](../files/Quasar/Services/Backup/BackupCompatibility.cs.md) | class (static) | Applies the semantic-versioning rules governing whether a backup may restore into the running Quasar: same major.minor is always allowed (patch may differ either way); an older major.minor is allowed only when a forward migration path exists; a newer major.minor is rejected (no cross-major.minor downgrade). Returns a `BackupCompatibilityResult` record struct. |
| [Quasar/Services/Backup/BackupFormatMigrations.cs](../files/Quasar/Services/Backup/BackupFormatMigrations.cs.md) | class (static) | Registry of forward upgrade steps (`BackupMigrationStep`) that migrate backup contents from one major.minor release to the next, with `CanMigrate` walking a contiguous chain from the backup version to the running version. The step list is empty today, so only same-major.minor restores are accepted until the first persisted-structure change ships. |
| [Quasar/Services/Backup/QuasarBackupService.cs](../files/Quasar/Services/Backup/QuasarBackupService.cs.md) | class | Builds and restores ZIP backups of Quasar's own configuration. Creates in-memory archives for download, writes timestamped ZIPs into the `Backups` directory, lists/prunes/deletes them, and restores by merging entries back over their on-disk paths after a version-compatibility check. Carries path-traversal and zip-slip guards, and reloads watcher-less catalogs after a restore. |
| [Quasar/Services/Backup/QuasarBackupSettingsService.cs](../files/Quasar/Services/Backup/QuasarBackupSettingsService.cs.md) | class | Singleton store for the automatic-backup schedule (`QuasarBackupSettings`), persisting to `backup-settings.json` via `AtomicFileWriter` and hot-reloading on external edits through a debounced `FileSystemWatcher`, mirroring `BrandingService`. Preserves the scheduler's own `LastBackupUtc` bookkeeping across UI saves. |

## Depends on

- [Magnetar.Protocol](Magnetar.Protocol.md)
- [Quasar.Models](Quasar.Models.md)
- [Quasar.Services.Core](Quasar.Services.Core.md)
