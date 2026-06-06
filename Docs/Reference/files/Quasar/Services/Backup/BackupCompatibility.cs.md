# Quasar/Services/Backup/BackupCompatibility.cs

**Module:** Quasar.Services.Backup  **Kind:** class (static) + record struct  **Tier:** 2

## Summary
Applies semantic-versioning rules deciding whether a backup may restore into the running Quasar. Same `Major.Minor` is always allowed (patch may differ in either direction); an older `Major.Minor` is allowed only if a forward migration path exists; a newer `Major.Minor` is rejected (no cross-major.minor downgrade).

## Structure
Namespace: `Quasar.Services.Backup`

`public readonly record struct BackupCompatibilityResult(bool Allowed, bool MigrationRequired, string Reason)`
`public static class BackupCompatibility`

| Member | Description |
|---|---|
| `Evaluate(string? backupVersion, string? runningVersion)` | Returns a `BackupCompatibilityResult` applying the version rules. |
| `CompareMajorMinor(Version a, Version b)` | Compares `Major` then `Minor`, ignoring patch. |

Private `TryParse` parses version strings.

## Dependencies
- [`Quasar/Services/Backup/BackupFormatMigrations.cs`](BackupFormatMigrations.cs.md)
- External: System.Version
