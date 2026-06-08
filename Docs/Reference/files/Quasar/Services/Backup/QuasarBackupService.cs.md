# Quasar/Services/Backup/QuasarBackupService.cs

**Module:** Quasar.Services.Backup  **Kind:** class  **Tier:** 1

## Summary
Builds and restores ZIP backups for three scopes: Quasar configuration, whole server data, and world-only data. Configuration backups still capture Quasar's own singleton/config/catalog files; server backups include the server definition, Dedicated Server app data, Magnetar app data, and world files including config; world backups restore world files while excluding `Sandbox_config.sbc*`.

## Structure
Namespace: `Quasar.Services.Backup`

`public sealed record QuasarBackupArchive(byte[] Content, string FileName)` — in-memory archive for download.
`public sealed record QuasarBackupFileInfo(string Name, long SizeBytes, DateTimeOffset CreatedAtUtc, QuasarBackupKind Kind, bool Automatic, string? ServerUniqueName, string? ServerDisplayName)` — stored-backup listing entry with manifest-derived type and target-server metadata.
`public sealed class QuasarBackupService`

Const `CurrentFormatVersion = 1`. All archives carry `quasar-backup.json`. Configuration layout uses `data/` plus `branding-assets/`. Server/world layouts use `server/server.json`, `dedicated-server/`, optional `dedicated-config/`, `magnetar/`, and/or `world/`. Filenames: `quasar-backup-{yyyyMMdd-HHmmss}{-auto?}.zip`, `quasar-server-{uniqueName}-{yyyyMMdd-HHmmss}{-auto?}.zip`, `quasar-world-{uniqueName}-{yyyyMMdd-HHmmss}{-auto?}.zip`. `JsonSerializerOptions`: Web + `WriteIndented`.

| Member | Description |
|---|---|
| `CreateBackup(DateTimeOffset timestamp)` | Builds a `QuasarBackupArchive` in memory with a timestamped download name (manual). |
| `WriteBackupFileAsync(DateTimeOffset timestamp, bool automatic, CancellationToken)` | Writes a ZIP into the Backups directory (`MagnetarPaths.GetQuasarBackupsDirectory()`); returns the file path. |
| `WriteServerBackupFileAsync(string uniqueName, DateTimeOffset timestamp, bool automatic, CancellationToken)` | Writes a server-scope ZIP including Quasar server definition, DS app data, Magnetar app data, and world files including world config. |
| `WriteWorldBackupFileAsync(string uniqueName, DateTimeOffset timestamp, bool automatic, CancellationToken)` | Writes a world-scope ZIP using the latest SE `Backup` snapshot when present, excluding `Sandbox_config.sbc*`. |
| `PruneAutomaticBackups(int retentionCount)` | Deletes oldest automatic Quasar config backups beyond `retentionCount`. |
| `PruneAutomaticBackups(QuasarBackupKind, int, string?)` | Deletes oldest automatic backups for one kind, optionally scoped to one server for server/world rules. |
| `ListBackups()` | `IReadOnlyList<QuasarBackupFileInfo>` enumerating `*.zip` in the Backups dir and reading manifests for kind/server metadata. |
| `ResolveBackupPath(string fileName)` | Validates a bare filename ending `.zip` that stays inside the Backups dir (path-traversal guard); returns full path or `null`. |
| `DeleteBackup(string fileName)` | Deletes a stored backup; returns `bool`. |
| `RestoreFromFileAsync(string fileName, CancellationToken)` | `Task<QuasarRestoreReport>` restoring from a stored backup file. |
| `RestoreAsync(Stream zipStream, CancellationToken)` | `Task<QuasarRestoreReport>`; copies to a temporary seekable file, reads the manifest, validates via `BackupCompatibility.Evaluate`, then dispatches restore by `BackupKind`. |

Constructor deps: `ILogger`, `WebServiceOptions _options`, `IWebHostEnvironment environment` (to resolve webRoot for the branding dir via `MagnetarPaths.GetQuasarBrandingDirectory(webRootPath)`), `KnownPlayerCatalog _knownPlayers`, `QuasarDevFolderCatalog _devFolders`, `DedicatedServerCatalog _servers`.

Configuration restore merges by overwriting files at their on-disk path (configs/templates/servers with new IDs added, matching IDs replaced). Server restore writes server/config/runtime/world entries to the target server paths; world restore requires the target server to exist and skips world config. Zip-slip guards keep all entries inside their resolved target roots. Configuration restore calls `_knownPlayers.ReloadFromDisk()` and `_devFolders.ReloadFromDisk()` (catalogs without a file watcher) and returns a report with `RestartRecommended = true`.

## Dependencies
- [`Magnetar.Protocol/Runtime/MagnetarPaths.cs`](../../../Magnetar.Protocol/Runtime/MagnetarPaths.cs.md)
- [`Quasar/Models/QuasarBackupManifest.cs`](../../Models/QuasarBackupManifest.cs.md)
- [`Quasar/Models/QuasarRestoreReport.cs`](../../Models/QuasarRestoreReport.cs.md)
- [`Quasar/Services/Backup/BackupCompatibility.cs`](BackupCompatibility.cs.md)
- [`Quasar/Services/WebServiceOptions.cs`](../WebServiceOptions.cs.md)
- [`Quasar/Services/KnownPlayerCatalog.cs`](../KnownPlayerCatalog.cs.md)
- [`Quasar/Services/QuasarDevFolderCatalog.cs`](../QuasarDevFolderCatalog.cs.md)
- [`Quasar/Services/DedicatedServerCatalog.cs`](../DedicatedServerCatalog.cs.md)
- External: System.IO.Compression (`ZipArchive`), System.Text.Json

## Notes
`ZipArchive` reads require a seekable stream, so browser uploads are copied to a temporary ZIP instead of buffered into memory. Server/world backups prefer the newest valid world directory under the SE `Backup` folder when present, avoiding live-save races. Path-traversal and zip-slip guards apply on both download and restore. Data-protection keys are NOT included in configuration archives, so an encrypted Steam Workshop API key must be re-entered when restoring on a different machine.
