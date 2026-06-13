# Quasar/Components/Pages/Backup.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page (`@page "/backup"`) for creating, restoring, scheduling and managing Quasar configuration, server, and world backups. Gated by `@attribute [Authorize(Policy = QuasarPolicyNames.CanManageSecurity)]` and `@implements IDisposable`.

## Structure
Namespace: `Quasar.Components.Pages`

Injected: `QuasarBackupService BackupService`, `QuasarBackupSettingsService BackupSettingsService`, `AutomaticBackupService AutomaticBackup`, `DedicatedServerCatalog ServerCatalog`, `WebServiceOptions WebServiceOptions`, `ISnackbar`, `IDialogService`.

UI sections (`MudGrid`):
1. **Create & restore** — "Create backup" links to `/api/backup/download`; restore via `MudFileUpload` (`.zip`, max 10 GB) → `RestoreFromUploadAsync`; shows the last restore-report alert with a restart recommendation. Explains that Quasar configuration backups cover app settings/catalog data, while server/world backups below cover game data and can be taken while a server runs because Quasar uses the latest SE `Backup` snapshot when present.
2. **Version compatibility** — same major.minor restores fully, older is upgraded via forward migration, newer is rejected; data-protection keys are excluded so the Steam Workshop API key is re-entered on a different machine.
3. **Server & world backups** — sortable table populated from `DedicatedServerCatalog`, one row per server. Each row has Back up server (`WriteServerBackupFileAsync`), Restore server (latest matching stored server backup), Back up world (`WriteWorldBackupFileAsync`), and Restore world (latest matching stored world backup). Server backups include server config; world backups exclude `Sandbox_config.sbc*`.
4. **Automatic backups** — three `MudExpansionPanel` rules: Quasar config, Servers, and Worlds. Each rule has its own enable switch, Frequency select (Hourly/Daily/Weekly), `MudTimePicker` time-of-day (Daily/Weekly), day-of-week select (Weekly), and retention numeric (config keeps last N total; server/world keep last N per server). Panels load expanded only when their rule is enabled. Buttons save all rules or run enabled rules immediately.
5. **Stored backups** table — sortable Name / Type / Server / Size / Created columns, defaulting newest first, with tooltip-wrapped Download (`/api/backup/download/{name}`), Restore and Delete actions.

`@code`: subscribes to `BackupSettingsService.Changed` and `ServerCatalog.Changed`; `LoadSettingsDraft` populates separate time-picker drafts for the three rules; `RefreshServers`, `RefreshBackups` (`BackupService.ListBackups()` sorted by timestamp descending), `SaveSettingsAsync`, `MakeBackupNowAsync` (`AutomaticBackup.RunEnabledBackupsNowAsync()`), row-scoped `MakeServerBackupNowAsync` / `MakeWorldBackupNowAsync`, latest-backup restore helpers, `RestoreFromUploadAsync` / `RestoreFromStoredAsync` (with backup-kind-specific confirm dialogs), `DeleteAsync` (confirm), and `FormatSize` / `FormatBackupType` helpers.

## Dependencies
- [`Quasar/Services/Backup/QuasarBackupService.cs`](../../Services/Backup/QuasarBackupService.cs.md)
- [`Quasar/Services/Backup/QuasarBackupSettingsService.cs`](../../Services/Backup/QuasarBackupSettingsService.cs.md)
- [`Quasar/Services/Backup/AutomaticBackupService.cs`](../../Services/Backup/AutomaticBackupService.cs.md)
- [`Quasar/Services/WebServiceOptions.cs`](../../Services/WebServiceOptions.cs.md)
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md)
- [`Quasar/Models/QuasarBackupSettings.cs`](../../Models/QuasarBackupSettings.cs.md)
- [`Quasar/Models/QuasarRestoreReport.cs`](../../Models/QuasarRestoreReport.cs.md)
- [`Quasar/Services/Auth/QuasarAuthConstants.cs`](../../Services/Auth/QuasarAuthConstants.cs.md) (`QuasarPolicyNames`)
- External: MudBlazor

## Notes
Download endpoints are policy-gated in `Program.cs`. Configuration restore overwrites settings sharing an ID with the backup (merge) and recommends a Quasar restart; server/world restore overwrites files for the target server and asks the operator to restart that server as needed.
