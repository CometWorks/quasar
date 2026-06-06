# Quasar/Models/QuasarBackupManifest.cs

**Module:** Quasar.Models  **Kind:** class  **Tier:** 3

## Summary
Metadata written to `quasar-backup.json` at the root of every backup ZIP.

## Structure
Namespace: `Quasar.Models`
`public sealed class QuasarBackupManifest`

| Member | Description |
|---|---|
| `FormatVersion` | `int` — archive layout version (not the Quasar version). |
| `QuasarVersion` | `string` — `Major.Minor.Build[.Revision]` the backup was saved from; drives semver compatibility on restore. |
| `CreatedAtUtc` | `DateTimeOffset` — when the backup was created. |
| `CreatedByHost` | `string?` — host that created the backup. |

## Dependencies
- Produced/consumed by `Quasar/Services/Backup/QuasarBackupService.cs`.
- No external packages.
