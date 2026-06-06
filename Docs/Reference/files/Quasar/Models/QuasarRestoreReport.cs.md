# Quasar/Models/QuasarRestoreReport.cs

**Module:** Quasar.Models  **Kind:** class  **Tier:** 3

## Summary
Outcome of a restore attempt, surfaced to the Backup page.

## Structure
Namespace: `Quasar.Models`
`public sealed class QuasarRestoreReport`

| Member | Description |
|---|---|
| `Success` | `bool` (init-only) — whether the restore succeeded. |
| `Message` | `string` (init-only) — human-readable outcome message. |
| `FilesRestored` | `int` (init-only) — number of files restored. |
| `BackupVersion` | `string?` (init-only) — version the backup was saved from. |
| `RunningVersion` | `string?` (init-only) — currently running Quasar version. |
| `RestartRecommended` | `bool` (init-only) — true after a successful restore; catalogs reload live, but a full restart guarantees every in-memory consumer picks up restored settings. |
| `Failed(string message, string? backupVersion = null, string? runningVersion = null)` | static — builds a failed report. |

## Dependencies
- Produced by `Quasar/Services/Backup/QuasarBackupService.cs`, consumed by `Quasar/Components/Pages/Backup.razor`.
- No external packages.
