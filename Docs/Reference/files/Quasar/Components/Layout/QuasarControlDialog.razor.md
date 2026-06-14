# Quasar/Components/Layout/QuasarControlDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Mobile-friendly MudBlazor dialog opened from the app bar power button. It presents the three Quasar power actions, then requires a second confirmation step with action-specific clarification before returning a `QuasarControlAction` to `MainLayout`.

## Structure
No route; rendered through `IDialogService.ShowAsync<QuasarControlDialog>()`.

**Parameters:**
- `IsRestartAvailable` — disables Restart Quasar when the worker was not launched by Bootstrap.
- `AgentOfflineShutdownSeconds` — shown as the grace period for servers running without Quasar after the Shutdown Quasar action.

**State:**
- `_pendingAction` — `null` while showing the action menu; set to a `QuasarControlAction` while showing the confirmation view.

**Actions:**
- Restart Quasar — confirms that servers continue to run, the UI briefly disconnects, and the worker is re-adopted after restart.
- Shutdown Quasar — confirms that the web UI/supervisor stops while servers remain detached, and explains the agent offline grace period.
- Shutdown all servers normally — confirms that Quasar stays online and each running server receives a normal graceful stop request.

## Dependencies
- [`Quasar/Components/Layout/QuasarControlAction.cs`](QuasarControlAction.cs.md)
- MudBlazor (`MudDialog`, `MudButton`, `MudAlert`, `IMudDialogInstance`, `DialogResult`)

## Notes
The component only gathers and confirms intent. `MainLayout` performs the actual shutdown/restart work after the dialog closes.
