# Quasar/Components/Pages/Home.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable root page (`/`) serving as the main dashboard. Shows a five-step first-run setup wizard that auto-opens only while no dedicated server has been created yet, a Managed Runtime panel with SteamCMD and Dedicated Server readiness/download progress, summary KPI cards (online servers, players online, health warnings, errors), an optional problem banner, and a `ServerCard` grid for all configured instances. Servers can be started, stopped, restarted, and opened in the log dialog directly from the dashboard, and the wizard opens full-screen page dialogs for config-template, world-template, and server creation.

## Structure
- **Route:** `@page "/"`; **Implements:** `IDisposable`
- **`[Inject]`:** `AgentRegistry Registry`, `DedicatedServerCatalog ServerCatalog`, `DedicatedServerSupervisor Supervisor`, `QuasarConfigProfileCatalog ConfigProfiles`, `QuasarWorldTemplateCatalog WorldTemplates`, `ManagedRuntimeWarmupService RuntimeWarmup`, `IDialogService DialogService`, `ISnackbar Snackbar`
- **Key UI sections**
  - Header with a "Restart Setup Wizard" button (shown only when the wizard is hidden).
  - Setup wizard (`MudPaper`, shown when `ShowSetupWizard`) with a Hide button, next-step hint alert, `MudProgressLinear` + progress text, and five sequential steps gated by `CurrentSetupStep`:
    1. Create config template — opens `ConfigsPageDialog` full-screen; Skip available.
    2. Import world template — opens `WorldTemplatesPageDialog` full-screen; Skip available.
    3. Create server — opens `ServersPageDialog` full-screen; lists up to 4 servers with setup summary/state chips.
    4. Start server — inline Start buttons per `StartableServers`, disabled states for "Create Server First" / "All Servers Running".
    5. Wait for Quasar.Agent — live state + agent-attach chips per `LaunchedServers`.
    Plus a Back button when past step 0.
  - Global health-monitoring info alert (`MudAlert`) — a single top-of-dashboard "Health monitoring disabled for this Quasar instance (development mode or configuration)" shown when `Supervisor.HealthMonitoringDisabled` is true, instead of repeating the message on every server card.
  - Problem banner (`MudAlert`) — first unhealthy/crashed/faulted, else first warning instance message.
  - Managed Runtime panel — visible only while warmup is pending/running/failed; hides after readiness completes. It shows overall warmup state plus SteamCMD and Dedicated Server component rows with status text, paths, and determinate/indeterminate `MudProgressLinear` progress. Start buttons are disabled until readiness is complete. A Retry button appears on the Dedicated Server row when that download fails.
  - KPI summary grid (4 cards): Online Servers, Players Online, Health Warnings (warning tint when > 0), Errors (error tint when > 0).
  - `ServerCard` grid — one `<ServerCard>` per server with runtime snapshot, connected agent, and `LaunchBlocked` readiness state; callbacks `StartRequested`/`StopRequested`/`RestartRequested`/`OpenLogsRequested`. When no servers and wizard hidden, shows an info alert.
- **Setup-wizard state (fields):** `_setupWizardRequested`, `_setupWizardDismissed`, `_setupWizardActive`, `_skippedSetupSteps` (HashSet), `_setupStepOverride`.
- **Wizard visibility:** `ShowSetupWizard => (_setupWizardActive || _setupWizardRequested) && !_setupWizardDismissed`. `OnInitialized` sets `_setupWizardActive = ConfiguredServerCount == 0`, so the wizard auto-opens only until the first server exists; once shown it stays for the session. `ShowSetupWizardAgain` re-requests it; `HideSetupWizard` dismisses it.
- **Step model:** `IsSetupStepComplete(0..4)` keyed on config-profile count / world-template count (or skipped) / server count / running count / connected-agent count; `CurrentSetupStep` returns the first incomplete step (or a clamped override); `SetupProgressPercent`, `SkipCurrentSetupStep`, `GoToPreviousSetupStep`.
- **KPI/state computed props:** `OnlineServerCount`, `PlayersOnline`, `ConfiguredServerCount`, `RunningServerCount`, `WarningServerCount`, `UnhealthyServerCount`, `ConnectedAgentCount`, `ProblemBanner`, `StartableServers`, `LaunchedServers`, `IsLaunchBlocked`.
- **Actions:** `StartAsync` blocks with a warning snackbar while managed runtime warmup is incomplete, otherwise sets goal state via `Supervisor.SetGoalStateAsync`; `RetryRuntimeWarmupAsync` calls `RuntimeWarmup.RetryAsync` from the Dedicated Server failed row; `StopAsync` sets goal Off; `RestartAsync` calls `Supervisor.RestartServerAsync`; `OpenLogsAsync` opens `ServerConsoleDialog`; all snackbar success/error where applicable. `ShowFullScreenPageDialogAsync<TDialog>` opens full-screen `MudDialog`s.
- **Helpers:** `GetRuntime`/`GetAgent`/`IsRunning`/`IsOpen`, `GetServerSetupSummary`, `GetSetupRuntimeSummary`, `GetSetup*Text`/`GetSetup*Color`, `GetRuntimeWarmup*`, `GetRuntimeComponent*`, `CanRetryRuntimeComponent`, `GetProblemCardClass`, `GetNextSetupHint`, `GetSetupProgressText`.
- **Event subscriptions** (subscribed in `OnInitialized`, released in `Dispose`): `Registry.Changed`, `ServerCatalog.Changed`, `Supervisor.Changed`, `ConfigProfiles.Changed`, `WorldTemplates.Changed`, `RuntimeWarmup.Changed`; `HandleRegistryChanged` marshals `StateHasChanged`.

## Dependencies
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md)
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md)
- [`Quasar/Services/DedicatedServerSupervisor.cs`](../../Services/DedicatedServerSupervisor.cs.md)
- `Quasar/Services/QuasarConfigProfileCatalog.cs`
- `Quasar/Services/QuasarWorldTemplateCatalog.cs`
- [`Quasar/Services/ManagedRuntimeWarmupService.cs`](../../Services/ManagedRuntimeWarmupService.cs.md)
- `Quasar/Components/ServerCard.razor` (child component)
- `Quasar/Components/Pages/ConfigsPageDialog.razor`, `WorldTemplatesPageDialog.razor`, `ServersPageDialog.razor` (full-screen wizard dialogs)
- `Magnetar.Protocol` — process/health/goal state enums, runtime snapshots
- MudBlazor (`MudProgressLinear`, `MudAlert`, `MudGrid`, `MudChip`, `MudPaper`, `IDialogService`, `ISnackbar`)

## Notes
- Server existence (`ConfiguredServerCount`) is the authoritative, persisted signal for auto-opening the wizard: once the first server is created the wizard never auto-opens again, but it can be reopened with "Restart Setup Wizard". There is no browser local-storage persistence of wizard state.
- The wizard is reactive: as servers start and agents attach, the steps advance automatically via the `Changed` subscriptions.
