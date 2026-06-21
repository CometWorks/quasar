# Quasar/Components/Pages/Backup.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page (`@page "/backup"`) for configuring the stored-backup folder and creating, restoring, scheduling, and managing Quasar configuration, server, and world backups. Gated by `@attribute [Authorize(Policy = QuasarPolicyNames.CanManageSecurity)]` and `@implements IDisposable`.

## Structure
Namespace: `Quasar.Components.Pages`

Injected: `QuasarBackupService BackupService`, `QuasarBackupSettingsService BackupSettingsService`, `AutomaticBackupService AutomaticBackup`, `DedicatedServerCatalog ServerCatalog`, `DedicatedServerSupervisor Supervisor`, `WebServiceOptions WebServiceOptions`, `ISnackbar`, `IDialogService`.

UI sections (`MudGrid`):
1. **Create & restore** — "Create backup" links to `/api/backup/download`; restore via `MudFileUpload` (`.zip`, max 10 GB) → `RestoreFromUploadAsync`; shows the last restore-report alert with a restart recommendation. Explains that Quasar configuration backups cover app settings/catalog data, while server backups cover non-cache runtime state and world backups cover save files.
2. **Version compatibility** — same major.minor restores fully, older is upgraded via forward migration, newer is rejected; data-protection keys are excluded so the Steam Workshop API key is re-entered on a different machine.
3. **Server & world backups** — sortable table populated from `DedicatedServerCatalog`, one row per server, with unique name, display name, and a rightmost unlabeled action group. Each row has Back up server (`AutomaticBackup.QueueServerBackup`), Restore server (latest matching stored server backup), Back up world (`AutomaticBackup.QueueWorldBackup`), and Restore world (latest matching stored world backup). Server/world backup buttons only enqueue background work and return immediately; server backups include server definition plus non-cache Dedicated Server/Magnetar app data, while world backups contain save files and exclude `Sandbox_config.sbc*`.
4. **Automatic backups** — three `MudExpansionPanel` rules: Quasar config, Servers, and Worlds. Each rule starts with explanatory copy describing what that backup scope contains: Quasar-managed config/catalog files, per-server non-cache DS/Magnetar app data, or world save files. Each rule has its own enable switch, Frequency select (Hourly/Daily/Weekly), `MudTimePicker` time-of-day (Daily/Weekly), day-of-week select (Weekly), and retention numeric (config keeps last N total; server/world keep last N per server). Panels load expanded only when their rule is enabled. Buttons save all rules or enqueue enabled rules to run immediately in the background.
5. **Stored backups** — shows the resolved configured backup folder with `CopyablePath`, an editable `Backup folder` field bound to `Quasar:BackupDirectory`, Browse (`FolderPickerDialog` with `RequireWorldFolder=false`), Use default, and Save folder actions. A `QUASAR_BACKUP_DIR` environment override makes the editor read-only. The stored-backup table lists Created / Type / Server / Size / Name columns, defaulting newest first, with tooltip-wrapped Download (`/api/backup/download/{name}`), Restore and Delete actions in a rightmost unlabeled action column.

`@code`: subscribes to `BackupSettingsService.Changed`, `BackupSettingsService.BackupDirectoryChanged`, `ServerCatalog.Changed`, `BackupService.Changed`, and `AutomaticBackup.QueuedBackupCompleted`; `LoadSettingsDraft` populates separate time-picker drafts for the three rules; `LoadBackupDirectoryDraft` reads appsettings/env override state for the folder editor; `RefreshServers`, `RefreshBackups` (`BackupService.ListBackups()` sorted by timestamp descending), `SaveSettingsAsync`, `SaveBackupDirectoryAsync`, `BrowseBackupDirectoryAsync`, `UseDefaultBackupDirectoryAsync`, `MakeBackupNowAsync` (`AutomaticBackup.QueueEnabledBackupsNow()`), row-scoped `MakeServerBackupNowAsync` / `MakeWorldBackupNowAsync` queue calls, latest-backup restore helpers, `RestoreFromUploadAsync` / `RestoreFromStoredAsync` (with backup-kind-specific confirm dialogs), `DeleteAsync` (confirm), and `FormatSize` / `FormatBackupType` helpers. `BackupService.Changed` refreshes the stored-backup list as each ZIP is atomically published; queued-job completion shows the success/error snackbar; backup-folder saves refresh the list against the newly active directory.

## Dependencies
- [`Quasar/Services/Backup/QuasarBackupService.cs`](../../Services/Backup/QuasarBackupService.cs.md)
- [`Quasar/Services/Backup/QuasarBackupSettingsService.cs`](../../Services/Backup/QuasarBackupSettingsService.cs.md)
- [`Quasar/Services/Backup/AutomaticBackupService.cs`](../../Services/Backup/AutomaticBackupService.cs.md)
- [`Quasar/Services/WebServiceOptions.cs`](../../Services/WebServiceOptions.cs.md)
- [`Magnetar.Protocol/Runtime/MagnetarPaths.cs`](../../../Magnetar.Protocol/Runtime/MagnetarPaths.cs.md)
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md)
- [`Quasar/Services/DedicatedServerSupervisor.cs`](../../Services/DedicatedServerSupervisor.cs.md)
- [`Quasar/Models/QuasarBackupSettings.cs`](../../Models/QuasarBackupSettings.cs.md)
- [`Quasar/Models/QuasarRestoreReport.cs`](../../Models/QuasarRestoreReport.cs.md)
- [`Quasar/Services/Auth/QuasarAuthConstants.cs`](../../Services/Auth/QuasarAuthConstants.cs.md) (`QuasarPolicyNames`)
- External: MudBlazor

## Notes
Download endpoints are policy-gated in `Program.cs`. Restore confirmations describe the selected scope: configuration backups merge matching IDs, server backups restore the server definition plus non-cache app data, and world backups restore save files while keeping existing config. Restored server definitions are written back with stopped intent by `QuasarBackupService`, so a restored backup cannot re-arm an `On` goal state before matching world files are restored. Manual stored-backup creation is fire-and-forget from the Blazor circuit, so page navigation is not blocked by ZIP creation. Folder saves create the target directory before applying it live; existing ZIPs are not moved between folders.
