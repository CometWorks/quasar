# Quasar/Components/Pages/Home.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable dashboard and primary server control surface at `/`. Shows a top-of-dashboard data handling consent prompt until YES/NO is stored, a five-step first-run setup wizard that auto-opens only while no dedicated server has been created yet, a Managed Runtime panel with SteamCMD and Dedicated Server readiness/download progress, summary KPI cards (online servers, players online, health warnings, errors), an optional problem banner, and a switchable server section. The default card view renders `ServerCard`s for live operations; `?view=list` embeds the `Servers` table without MudBlazor's responsive card layout. Only the selected server view is generated, so the landing page does not build hidden list/table DOM. Servers can be started, stopped, restarted, and opened in the log dialog directly from the dashboard; Stop and Kill actions require confirmation. The wizard opens full-screen page dialogs for config-template, world-template, and server creation.

## Structure
- **Route:** `@page "/"`; **Implements:** `IDisposable`
- **`[Inject]`:** `AgentRegistry Registry`, `DedicatedServerCatalog ServerCatalog`, `DedicatedServerSupervisor Supervisor`, `QuasarConfigProfileCatalog ConfigProfiles`, `QuasarWorldTemplateCatalog WorldTemplates`, `ManagedRuntimeWarmupService RuntimeWarmup`, `DataHandlingConsentCatalog DataHandlingConsent`, `IDialogService DialogService`, `ISnackbar Snackbar`, `NavigationManager Navigation`
- **`[SupplyParameterFromQuery(Name = "view")]`:** `ServerViewQuery` — `list` selects the embedded server-management table; any other value keeps the default card layout.
- **Key UI sections**
  - Data Handling Consent prompt — top `MudPaper` shown only while `DataHandlingConsent.GetSettings().ConsentGranted` is `null`; asks YES/NO with equal outlined buttons and saves through `DataHandlingConsent.SaveAsync`. It explains that no stored decision means Magnetar starts with no consent.
  - Compact top row with only a "Restart Setup Wizard" button when the wizard is hidden; there is no visible "Dashboard" page title.
  - Setup wizard (`MudPaper`, shown when `ShowSetupWizard`) with a Hide button, next-step hint alert, `MudProgressLinear` + progress text, and five sequential steps gated by `CurrentSetupStep`:
    1. Create config template — opens `ConfigsPageDialog` full-screen; Skip available.
    2. Import world template — opens `WorldTemplatesPageDialog` full-screen; Skip available.
    3. Create server — opens `ServersPageDialog` full-screen; lists up to 4 servers with setup summary/state chips.
    4. Start server — inline Start buttons per `StartableServers`, disabled states for "Create Server First" / "All Servers Running".
    5. Wait for Quasar.Agent — live state + agent-attach chips per `LaunchedServers`.
    Plus a Back button when past step 0.
  - Global health-monitoring info alert (`MudAlert`) — a single top-of-dashboard "Health monitoring disabled for this Quasar instance (development mode or configuration)" shown when `Supervisor.HealthMonitoringDisabled` is true, instead of repeating the message on every server card.
  - Problem banner (`MudAlert`) — first unhealthy/crashed/faulted, else first warning instance message.
  - Managed Runtime panel — visible only while warmup is pending/running/failed; hides after readiness completes. It shows overall warmup state plus SteamCMD and Dedicated Server component rows with status text, copyable diagnostic paths, and determinate/indeterminate `MudProgressLinear` progress. Start buttons are disabled until readiness is complete. A Retry button appears on the Dedicated Server row when that download fails.
  - KPI summary grid (4 cards): Online Servers, Players Online, Health Warnings (warning tint when > 0), Errors (error tint when > 0).
  - Server view toolbar — title plus Cards/List buttons. `SetServerView` updates the URL to `/` or `/?view=list` with replace navigation.
  - Card view — one `<ServerCard>` per server with runtime snapshot, connected agent, and `LaunchBlocked` readiness state; callbacks `StartRequested`/`StopRequested`/`KillStartingRequested`/`RestartRequested`. Cards provide their own clone/delete/edit/template/console buttons through `ServerManagementActions`. When no servers and wizard hidden, shows an info alert.
  - List view — embeds `<Servers Embedded HideHeader DisableResponsiveLayout ...>` so the server-management table appears under the dashboard status cards and stays a true table with horizontal scrolling instead of auto-switching to MudBlazor's narrow-card presentation. Start buttons inherit the dashboard runtime-warmup block message.
- **Setup-wizard state (fields):** `_setupWizardRequested`, `_setupWizardDismissed`, `_setupWizardActive`, `_skippedSetupSteps` (HashSet), `_setupStepOverride`.
- **Wizard visibility:** `ShowSetupWizard => (_setupWizardActive || _setupWizardRequested) && !_setupWizardDismissed`. `OnInitialized` sets `_setupWizardActive = ConfiguredServerCount == 0`, so the wizard auto-opens only until the first server exists; once shown it stays for the session. `ShowSetupWizardAgain` re-requests it; `HideSetupWizard` dismisses it.
- **Step model:** `IsSetupStepComplete(0..4)` keyed on config-profile count / world-template count (or skipped) / server count / running count / connected-agent count; `CurrentSetupStep` returns the first incomplete step (or a clamped override); `SetupProgressPercent`, `SkipCurrentSetupStep`, `GoToPreviousSetupStep`.
- **KPI/state computed props:** `OnlineServerCount`, `PlayersOnline`, `ConfiguredServerCount`, `RunningServerCount`, `WarningServerCount`, `UnhealthyServerCount`, `ConnectedAgentCount`, `ProblemBanner`, `StartableServers`, `LaunchedServers`, `IsLaunchBlocked`, `ShowDataHandlingConsentPrompt`, `ServerView`, `IsCardsView`, `IsListView`.
- **Actions:** `SaveDataHandlingConsentAsync` persists the YES/NO choice and reports success/failure; `StartAsync` blocks with a warning snackbar while managed runtime warmup is incomplete, otherwise sets goal On and explicitly starts via `Supervisor.StartServerAsync` so `Crashed`/`Faulted` can be operator-retried; `RetryRuntimeWarmupAsync` calls `RuntimeWarmup.RetryAsync` from the Dedicated Server failed row; `StopAsync` confirms then sets goal Off; `KillStartingAsync` confirms then calls `Supervisor.KillStartingServerAsync`; `RestartAsync` calls `Supervisor.RestartServerAsync`; `SetServerView` switches card/list URL state; `OpenConfigProfileFromServerListAsync` opens `ConfigsPageDialog` with `InitialProfileId` when a config link is clicked from the embedded list. `ShowFullScreenPageDialogAsync<TDialog>` opens full-screen `MudDialog`s and accepts optional `DialogParameters`.
- **Helpers:** `GetRuntime`/`GetAgent`/`IsRunning`/`IsOpen`, `GetServerSetupSummary`, `GetSetupRuntimeSummary`, `GetSetup*Text`/`GetSetup*Color`, `GetRuntimeWarmup*`, `GetRuntimeComponent*`, `CanRetryRuntimeComponent`, `GetProblemCardClass`, `GetNextSetupHint`, `GetSetupProgressText`.
- **Event subscriptions** (subscribed in `OnInitialized`, released in `Dispose`): `Registry.Changed`, `ServerCatalog.Changed`, `Supervisor.Changed`, `ConfigProfiles.Changed`, `WorldTemplates.Changed`, `RuntimeWarmup.Changed`, `DataHandlingConsent.Changed`; `HandleRegistryChanged` marshals `StateHasChanged`.

## Dependencies
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md)
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md)
- [`Quasar/Services/DedicatedServerSupervisor.cs`](../../Services/DedicatedServerSupervisor.cs.md)
- `Quasar/Services/QuasarConfigProfileCatalog.cs`
- `Quasar/Services/QuasarWorldTemplateCatalog.cs`
- [`Quasar/Services/ManagedRuntimeWarmupService.cs`](../../Services/ManagedRuntimeWarmupService.cs.md)
- [`Quasar/Services/WebServiceOptions.cs`](../../Services/WebServiceOptions.cs.md) — `DataHandlingConsentCatalog`
- [`Quasar/Components/Shared/CopyablePath.razor`](../Shared/CopyablePath.razor.md)
- `Quasar/Components/ServerCard.razor` (card-view child component)
- [`Quasar/Components/Pages/Servers.razor`](Servers.razor.md) (embedded list view)
- `Quasar/Components/Pages/ConfigsPageDialog.razor`, `WorldTemplatesPageDialog.razor`, `ServersPageDialog.razor` (full-screen wizard dialogs)
- `Magnetar.Protocol` — process/health/goal state enums, runtime snapshots
- MudBlazor (`MudProgressLinear`, `MudAlert`, `MudGrid`, `MudChip`, `MudPaper`, `IDialogService`, `ISnackbar`)

## Notes
- Server existence (`ConfiguredServerCount`) is the authoritative, persisted signal for auto-opening the wizard: once the first server is created the wizard never auto-opens again, but it can be reopened with "Restart Setup Wizard". There is no browser local-storage persistence of wizard state.
- The wizard is reactive: as servers start and agents attach, the steps advance automatically via the `Changed` subscriptions.
