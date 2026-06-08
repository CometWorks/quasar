# Quasar/Models/QuasarBackupSettings.cs

**Module:** Quasar.Models  **Kind:** enum + class  **Tier:** 2

## Summary
Persistent config for the automatic backup scheduler, serialized to `backup-settings.json`. Defines the backup frequency enum, one reusable rule record, and the settings object containing separate Quasar config, server, and world automatic-backup rules.

## Structure
Namespace: `Quasar.Models`
`public enum BackupFrequency { Hourly, Daily, Weekly }`
`public sealed class QuasarBackupRuleSettings`
`public sealed class QuasarBackupSettings`

| Member | Description |
|---|---|
| `QuasarBackupRuleSettings.MinRetentionCount/MaxRetentionCount/DefaultRetentionCount` | Retention bounds shared by every rule (`1`, `1000`, `10`). |
| `QuasarBackupRuleSettings.Enabled` | `bool` — whether this rule runs. |
| `QuasarBackupRuleSettings.Frequency` | `BackupFrequency` — default `Daily`. |
| `QuasarBackupRuleSettings.TimeOfDay` | `TimeOnly` — default `03:00`; used for Daily/Weekly, ignored for Hourly. |
| `QuasarBackupRuleSettings.DayOfWeek` | `DayOfWeek` — default `Sunday`; Weekly only. |
| `QuasarBackupRuleSettings.RetentionCount` | `int` — most-recent backups to keep (default 10). |
| `QuasarBackupRuleSettings.LastBackupUtc` | `DateTimeOffset?` — last automatic backup for this rule; used to compute next due. |
| `Configuration` | `QuasarBackupRuleSettings` — automatic Quasar config backup rule. |
| `Server` | `QuasarBackupRuleSettings` — automatic server backup rule, applied to every configured server. |
| `World` | `QuasarBackupRuleSettings` — automatic world backup rule, applied to every configured server. |
| legacy flat properties | Nullable properties read from old `backup-settings.json` files and migrated into `Configuration` by `Normalize`; not written by normal saves. |
| `Clone()` | Returns a deep copy of the settings. |
| `Normalize(QuasarBackupSettings?)` | static — normalizes all rules and migrates legacy flat settings. |
| `GetRule(QuasarBackupKind)` | Returns the rule matching a backup kind. |

## Dependencies
- Used by [`Quasar/Services/Backup/QuasarBackupSettingsService.cs`](../Services/Backup/QuasarBackupSettingsService.cs.md) and [`Quasar/Services/Backup/AutomaticBackupService.cs`](../Services/Backup/AutomaticBackupService.cs.md).
- No external packages.
