# Quasar/Services/Backup/QuasarBackupService.cs

**Module:** Quasar.Services.Backup  **Kind:** class  **Tier:** 1

## Summary
Builds and restores ZIP backups of Quasar's *own* configuration. A backup captures servers, config profiles, world templates and all app settings (Discord, branding, known players, security/RBAC, dev folders), but deliberately excludes game servers, worlds, plugin configs, runtime state, logs and history. Restore merges the archive into the running install by overwriting files at their on-disk path.

## Structure
Namespace: `Quasar.Services.Backup`

`public sealed record QuasarBackupArchive(byte[] Content, string FileName)` — in-memory archive for download.
`public sealed record QuasarBackupFileInfo(string Name, long SizeBytes, DateTimeOffset CreatedAtUtc, bool Automatic)` — stored-backup listing entry.
`public sealed class QuasarBackupService`

Const `CurrentFormatVersion = 1`. Archive layout: `quasar-backup.json` manifest at root, `data/` mirror of config files, `branding-assets/` copy of uploaded logo/favicon images. Singleton config files included if present: `known-players.json`, `discord.json`, `death-messages.json`, `branding.json`, `steam-workshop.json`, `rbac.json`, `dev-folders.json`. Per-entity definition files: `Magnetars/**/server.json`, `ConfigProfiles/**/profile.json`, `WorldTemplates/**/template.json` (fixed filenames naturally exclude `History/` and `World/` snapshots). Filename format: `quasar-backup-{yyyyMMdd-HHmmss}{-auto?}.zip`. `JsonSerializerOptions`: Web + `WriteIndented`.

| Member | Description |
|---|---|
| `CreateBackup(DateTimeOffset timestamp)` | Builds a `QuasarBackupArchive` in memory with a timestamped download name (manual). |
| `WriteBackupFileAsync(DateTimeOffset timestamp, bool automatic, CancellationToken)` | Writes a ZIP into the Backups directory (`MagnetarPaths.GetQuasarBackupsDirectory()`); returns the file path. |
| `PruneAutomaticBackups(int retentionCount)` | Deletes oldest automatic backups (files ending `-auto.zip`) beyond `retentionCount`; relies on timestamped filename sorting. |
| `ListBackups()` | `IReadOnlyList<QuasarBackupFileInfo>` enumerating `*.zip` in the Backups dir; `Automatic` flag set if name ends with `-auto`. |
| `ResolveBackupPath(string fileName)` | Validates a bare filename ending `.zip` that stays inside the Backups dir (path-traversal guard); returns full path or `null`. |
| `DeleteBackup(string fileName)` | Deletes a stored backup; returns `bool`. |
| `RestoreFromFileAsync(string fileName, CancellationToken)` | `Task<QuasarRestoreReport>` restoring from a stored backup file. |
| `RestoreAsync(Stream zipStream, CancellationToken)` | `Task<QuasarRestoreReport>`; copies to a seekable `MemoryStream`, reads the manifest, validates via `BackupCompatibility.Evaluate(manifest.QuasarVersion, _options.Version)`, then merges. |

Constructor deps: `ILogger`, `WebServiceOptions _options`, `IWebHostEnvironment environment` (to resolve webRoot for the branding dir via `MagnetarPaths.GetQuasarBrandingDirectory(webRootPath)`), `KnownPlayerCatalog _knownPlayers`, `QuasarDevFolderCatalog _devFolders`.

Restore merges by overwriting files at their on-disk path (configs/templates/servers with new IDs added, matching IDs replaced). A zip-slip guard (`ResolveExtractionTarget`) keeps entries under `quasarRoot` (`data/`) or `brandingRoot` (`branding-assets/`). After extraction it calls `_knownPlayers.ReloadFromDisk()` and `_devFolders.ReloadFromDisk()` (catalogs without a file watcher) and returns a report with `RestartRecommended = true`.

## Dependencies
- [`Magnetar.Protocol/Runtime/MagnetarPaths.cs`](../../../Magnetar.Protocol/Runtime/MagnetarPaths.cs.md)
- [`Quasar/Models/QuasarBackupManifest.cs`](../../Models/QuasarBackupManifest.cs.md)
- [`Quasar/Models/QuasarRestoreReport.cs`](../../Models/QuasarRestoreReport.cs.md)
- [`Quasar/Services/Backup/BackupCompatibility.cs`](BackupCompatibility.cs.md)
- [`Quasar/Services/WebServiceOptions.cs`](../WebServiceOptions.cs.md)
- [`Quasar/Services/KnownPlayerCatalog.cs`](../KnownPlayerCatalog.cs.md)
- [`Quasar/Services/QuasarDevFolderCatalog.cs`](../QuasarDevFolderCatalog.cs.md)
- External: System.IO.Compression (`ZipArchive`), System.Text.Json

## Notes
`ZipArchive` reads require a seekable stream, so browser uploads are buffered into memory. Path-traversal and zip-slip guards apply on both download and restore. Data-protection keys are NOT included in the archive, so an encrypted Steam Workshop API key must be re-entered when restoring on a different machine.
