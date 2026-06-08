# Quasar/Components/Pages/Updates.razor

**Module:** Quasar.Components  **Kind:** component  **Tier:** 2

## Summary

Routable MudBlazor page at `/settings/updates` for checking, staging, and activating Quasar Linux release updates. It shows current update status from `QuasarUpdateService`, separates Quasar UI and launcher candidates, exposes manual check/stage/activate actions for the UI worker, shows launcher update availability, displays configured GitHub release source and asset names, and provides a warning-gated switch for including prerelease versions in the update stream.

## Structure

Route: `/settings/updates`  
Authorization: `QuasarPolicyNames.CanManageSecurity`

**Injected services**

- `QuasarUpdateService` — snapshot source and action API
- `QuasarUpdateOptions` — configured GitHub owner/repository/assets/check interval
- `WebServiceOptions` — current UI and Bootstrap versions
- `ISnackbar` — user feedback for update actions
- `IDialogService` — confirmation dialog before enabling prerelease updates

**Key members**

| Member | Description |
|---|---|
| `OnInitialized()` / `Dispose()` | Subscribes/unsubscribes to `UpdateService.Changed` and initializes `_snapshot`. |
| `CheckNowAsync()` | Runs an immediate release check through `QuasarUpdateService.CheckNowAsync()`. |
| `StageAsync()` | Downloads and stages the queued Quasar UI update. |
| `ActivateAsync()` | Requests staged UI activation; the update service promotes the staged payload into the managed active-release directory and writes the active-release pointer. The Activate button is disabled when the staged UI version is not newer than the running UI worker. |
| `HandleIncludePrereleaseChanged(bool)` | Confirms before enabling prerelease updates, persists the stream setting through `QuasarUpdateService`, and shows a strong warning while prereleases are enabled. |
| `RunBusyAsync(...)` | Shared busy-state/error/snackbar wrapper for the three actions. |
| `GetStatusSeverity()` | Maps `QuasarUpdateStatus` to MudBlazor alert severity. |
| `FormatBootstrapVersion()` | Shows the Bootstrap launcher version when the worker was started by Bootstrap, otherwise reports that Bootstrap is not managing this worker. |
| `IsNewerUiVersion(...)` | Uses `QuasarReleaseVersion.IsNewer` so the Activate button is only enabled for staged UI versions newer than `CurrentVersion`. |

## Dependencies

- [`Quasar/Services/Updates/QuasarUpdateService.cs`](../../Services/Updates/QuasarUpdateService.cs.md) — update checks, staging, activation
- [`Quasar/Services/Updates/QuasarUpdateOptions.cs`](../../Services/Updates/QuasarUpdateOptions.cs.md) — release source and asset names
- [`Quasar/Services/Updates/QuasarUpdateSnapshot.cs`](../../Services/Updates/QuasarUpdateSnapshot.cs.md) — status/candidate DTOs displayed by the page
- `Quasar/Services/WebServiceOptions.cs` — current Quasar UI and Bootstrap versions
