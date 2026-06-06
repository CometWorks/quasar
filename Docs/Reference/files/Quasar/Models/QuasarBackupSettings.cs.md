# Quasar/Models/QuasarBackupSettings.cs

**Module:** Quasar.Models  **Kind:** enum + class  **Tier:** 2

## Summary
Persistent config for the automatic backup scheduler, serialized to `backup-settings.json`. Defines the backup frequency enum and the settings record with retention bounds and scheduling fields.

## Structure
Namespace: `Quasar.Models`
`public enum BackupFrequency { Hourly, Daily, Weekly }`
`public sealed class QuasarBackupSettings`

| Member | Description |
|---|---|
| `MinRetentionCount` | `const` = 1. |
| `MaxRetentionCount` | `const` = 1000. |
| `DefaultRetentionCount` | `const` = 10. |
| `Enabled` | `bool` — whether automatic backups run. |
| `Frequency` | `BackupFrequency` — default `Daily`. |
| `TimeOfDay` | `TimeOnly` — default `03:00`; used for Daily/Weekly, ignored for Hourly. |
| `DayOfWeek` | `DayOfWeek` — default `Sunday`; Weekly only. |
| `RetentionCount` | `int` — most-recent backups to keep (default 10). |
| `LastBackupUtc` | `DateTimeOffset?` — last automatic backup; used to compute next due. |
| `Clone()` | Returns a copy of the settings. |
| `Normalize(QuasarBackupSettings?)` | static — clamps `RetentionCount` to `[Min, Max]`. |

## Dependencies
- Used by `Quasar/Services/Backup/QuasarBackupSettingsService.cs` and `Quasar/Services/Backup/AutomaticBackupService.cs`.
- No external packages.
