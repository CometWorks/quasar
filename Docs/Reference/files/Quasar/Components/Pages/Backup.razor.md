# Quasar/Components/Pages/Backup.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page (`@page "/backup"`) for creating, restoring, scheduling and managing Quasar configuration backups. Gated by `@attribute [Authorize(Policy = QuasarPolicyNames.CanManageSecurity)]` and `@implements IDisposable`.

## Structure
Namespace: `Quasar.Components.Pages`

Injected: `QuasarBackupService BackupService`, `QuasarBackupSettingsService BackupSettingsService`, `AutomaticBackupService AutomaticBackup`, `WebServiceOptions WebServiceOptions`, `ISnackbar`, `IDialogService`.

UI sections (`MudGrid`):
1. **Create & restore** — "Create backup" links to `/api/backup/download`; restore via `MudFileUpload` (`.zip`, max 256 MB) → `RestoreFromUploadAsync`; shows the last restore-report alert with a restart recommendation. Explains a backup contains servers, config profiles, world templates and all app settings (Discord, branding, players, security/RBAC) but NOT game servers/worlds/plugin configs; restore MERGES by ID.
2. **Version compatibility** — same major.minor restores fully, older is upgraded via forward migration, newer is rejected; data-protection keys are excluded so the Steam Workshop API key is re-entered on a different machine.
3. **Automatic backups** — enable switch, Frequency select (Hourly/Daily/Weekly), `MudTimePicker` time-of-day (Daily/Weekly), day-of-week select (Weekly), retention numeric (Keep last N, min/max from `QuasarBackupSettings`), Save schedule + "Make a backup now" buttons.
4. **Stored backups** table — Name / Type (Automatic | Manual) / Size / Created, with Download (`/api/backup/download/{name}`), Restore and Delete actions.

`@code`: subscribes to `BackupSettingsService.Changed`; `LoadSettingsDraft`, `RefreshBackups` (`BackupService.ListBackups()`), `SaveSettingsAsync`, `MakeBackupNowAsync` (`AutomaticBackup.RunBackupNowAsync()`), `RestoreFromUploadAsync` / `RestoreFromStoredAsync` (with confirm dialog), `DeleteAsync` (confirm), and a `FormatSize` helper.

## Dependencies
- [`Quasar/Services/Backup/QuasarBackupService.cs`](../../Services/Backup/QuasarBackupService.cs.md)
- [`Quasar/Services/Backup/QuasarBackupSettingsService.cs`](../../Services/Backup/QuasarBackupSettingsService.cs.md)
- [`Quasar/Services/Backup/AutomaticBackupService.cs`](../../Services/Backup/AutomaticBackupService.cs.md)
- [`Quasar/Services/WebServiceOptions.cs`](../../Services/WebServiceOptions.cs.md)
- [`Quasar/Models/QuasarBackupSettings.cs`](../../Models/QuasarBackupSettings.cs.md)
- [`Quasar/Models/QuasarRestoreReport.cs`](../../Models/QuasarRestoreReport.cs.md)
- [`Quasar/Services/Auth/QuasarAuthConstants.cs`](../../Services/Auth/QuasarAuthConstants.cs.md) (`QuasarPolicyNames`)
- External: MudBlazor

## Notes
Download endpoints are policy-gated in `Program.cs`. Restore overwrites settings sharing an ID with the backup (merge) and recommends a Quasar restart.
