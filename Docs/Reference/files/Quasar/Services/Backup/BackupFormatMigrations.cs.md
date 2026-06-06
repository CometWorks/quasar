# Quasar/Services/Backup/BackupFormatMigrations.cs

**Module:** Quasar.Services.Backup  **Kind:** class (static)  **Tier:** 2

## Summary
Registry of forward upgrade steps that migrate backup contents from one major.minor release to the next. The registry is currently empty, so only same-major.minor restores are accepted today.

## Structure
Namespace: `Quasar.Services.Backup`

`public static class BackupFormatMigrations`
Nested `public sealed record BackupMigrationStep(Version From, Version To)`

| Member | Description |
|---|---|
| `Steps` | `IReadOnlyList<BackupMigrationStep>`, currently `Array.Empty` — no migrations exist yet. When a future release changes a persisted structure, add a step and teach `QuasarBackupService` to apply the chain. |
| `CanMigrate(Version backupVersion, Version runningVersion)` | Walks the registered steps from the backup's major.minor toward the running major.minor, returning `true` if a contiguous chain reaches the target. |

## Dependencies
- [`Quasar/Services/Backup/BackupCompatibility.cs`](BackupCompatibility.cs.md) (uses `CompareMajorMinor`)
- External: System.Version
