# Quasar/Models/QuasarBackupManifest.cs

**Module:** Quasar.Models  **Kind:** enum + class  **Tier:** 3

## Summary
Metadata written to `quasar-backup.json` at the root of every backup ZIP, including the backup scope and optional target-server identity.

## Structure
Namespace: `Quasar.Models`
`public enum QuasarBackupKind`
`public sealed class QuasarBackupManifest`

| Member | Description |
|---|---|
| `QuasarBackupKind` | enum — `Configuration`, `Server`, or `World`. |
| `FormatVersion` | `int` — archive layout version (not the Quasar version). |
| `QuasarVersion` | `string` — `Major.Minor.Build[.Revision]` the backup was saved from; drives semver compatibility on restore. |
| `CreatedAtUtc` | `DateTimeOffset` — when the backup was created. |
| `CreatedByHost` | `string?` — host that created the backup. |
| `BackupKind` | `QuasarBackupKind` — restore dispatcher scope; defaults to `Configuration` for backward compatibility with old manifests. |
| `ServerUniqueName` | `string?` — target server for server/world backups. |
| `ServerDisplayName` | `string?` — display label for the target server in the Backup UI. |

## Dependencies
- Produced/consumed by [`Quasar/Services/Backup/QuasarBackupService.cs`](../Services/Backup/QuasarBackupService.cs.md).
- No external packages.
