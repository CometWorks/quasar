# Quasar/Components/Pages/Updates.razor

**Module:** Quasar.Components  **Kind:** component  **Tier:** 2

## Summary

Routable MudBlazor page at `/settings/updates` for checking, staging, activating, and rolling back Quasar UI worker releases plus inspecting/updating managed runtime components. It shows current Quasar update status from `QuasarUpdateService`, separates selectable Quasar UI releases from launcher candidates, exposes manual Quasar check/stage/activate actions, can force a detected Bootstrap launcher update, displays installed Magnetar and Space Engineers Dedicated Server versions/paths at all times, exposes separate manual Magnetar and DS checks, provides prerelease/auto-staging controls, and renders a git-style `appsettings.json` conflict editor when staging cannot auto-merge settings.

## Structure

Route: `/settings/updates`  
Authorization: `QuasarPolicyNames.CanManageSecurity`

**Injected services**

- `QuasarUpdateService` — snapshot source and action API
- `QuasarUpdateOptions` — configured GitHub owner/repository/assets/check interval
- `WebServiceOptions` — current UI and Bootstrap versions
- `ManagedRuntimeWarmupService` — managed Magnetar/DS snapshot plus manual update checks
- `ISnackbar` — user feedback for update actions
- `IDialogService` — confirmation dialogs before enabling prerelease updates or forcing Bootstrap activation
- `IJSRuntime` — starts browser-side health polling before UI-worker activation or a forced Bootstrap restart drops the circuit

**Key members**

| Member | Description |
|---|---|
| `OnInitialized()` / `Dispose()` | Subscribes/unsubscribes to `UpdateService.Changed` and `RuntimeWarmup.Changed`; initializes Quasar and managed-runtime snapshots. |
| `CheckNowAsync()` | Runs an immediate Quasar release check through `QuasarUpdateService.CheckNowAsync()`. |
| `CheckMagnetarNowAsync()` | Runs an immediate managed Magnetar check through `ManagedRuntimeWarmupService.CheckMagnetarNowAsync()`. |
| `CheckDedicatedServerNowAsync()` | Runs an immediate managed DS check through `ManagedRuntimeWarmupService.CheckDedicatedServerNowAsync()`. |
| `HandleSelectedWebVersionChanged(...)` | Selects the UI release to stage/install from the discovered list, including older rollback targets. |
| `StageAsync()` | Downloads and stages the selected Quasar UI release unless it is already current or staged; if appsettings rollover conflicts, loads the conflict text and warns instead of reporting success. |
| `ActivateAsync()` | Starts browser health polling for the selected target version, then requests staged UI activation; disabled outside Bootstrap because no launcher is watching the active-release pointer. Older staged releases are allowed for rollback. |
| `ForceActivateBootstrapAsync()` | Confirms, starts browser health polling that waits for a restart gap, then asks `QuasarUpdateService` to write the Bootstrap update request file so the launcher activates the detected update immediately. Disabled when no Bootstrap update is detected or the worker is not launcher-managed. |
| `HandleIncludePrereleaseChanged(bool)` | Confirms before enabling prerelease updates, persists the stream setting through `QuasarUpdateService`, refreshes the release list, and shows a strong warning while prereleases are enabled. |
| `HandleAutoStageWebUpdatesChanged(bool)` | Persists whether release checks should automatically download/stage a newer UI release or only queue releases for manual staging. |
| `LoadAppSettingsConflictAsync()` / `SaveAppSettingsResolutionAsync()` / `ForceReleaseAppSettingsAsync()` | Reads the staged conflict file, saves a manually resolved JSON file, or force-restages release defaults after confirmation. |
| `RunBusyAsync(...)` | Shared busy-state/error/snackbar wrapper for Quasar update actions. |
| `RunRuntimeBusyAsync(...)` | Shared busy-state/error/snackbar wrapper for managed-runtime checks. |
| `GetStatusSeverity()` | Maps `QuasarUpdateStatus` to MudBlazor alert severity. |
| `ManagedRuntimeRows` / `FormatRuntimeVersion(...)` | Render installed managed-runtime version/path/status rows for Magnetar and Dedicated Server. |
| `FormatBootstrapVersion()` | Shows the Bootstrap launcher version when the worker was started by Bootstrap, otherwise reports that Bootstrap is not managing this worker. |
| `FormatWebReleaseOption(...)` / `FormatWebReleaseStatus(...)` | Labels selectable UI releases as current, newer, rollback, prerelease, and/or staged. |

## Dependencies

- [`Quasar/Services/Updates/QuasarUpdateService.cs`](../../Services/Updates/QuasarUpdateService.cs.md) — update checks, staging, activation
- [`Quasar/Services/Updates/QuasarUpdateOptions.cs`](../../Services/Updates/QuasarUpdateOptions.cs.md) — release source and asset names
- [`Quasar/Services/Updates/QuasarUpdateSnapshot.cs`](../../Services/Updates/QuasarUpdateSnapshot.cs.md) — status/candidate DTOs displayed by the page
- [`Quasar/Components/Shared/CopyablePath.razor`](../Shared/CopyablePath.razor.md)
- `Quasar/Services/WebServiceOptions.cs` — current Quasar UI and Bootstrap versions plus launcher-managed detection
- [`Quasar/Services/ManagedRuntimeWarmupService.cs`](../../Services/ManagedRuntimeWarmupService.cs.md) — managed-runtime versions and manual checks
