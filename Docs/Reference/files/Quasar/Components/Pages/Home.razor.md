# Quasar/Components/Pages/Home.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable root page (`/`) serving as the main dashboard. Shows a five-step first-run setup wizard (persisted dismissed/completed state in browser local storage), summary KPI cards (online servers, players, health warnings, errors), an optional problem banner, a managed-runtime warmup status alert, and a `ServerCard` grid for all configured instances. Supports starting, stopping, and restarting servers directly from the dashboard, and opens full-screen page dialogs for config-template, world-template, and server creation from within the setup wizard.

## Structure
- **Route:** `@page "/"`
- **Implements:** `IDisposable`
- **Injected services:** `AgentRegistry`, `DedicatedServerCatalog`, `DedicatedServerSupervisor`, `QuasarConfigProfileCatalog`, `QuasarWorldTemplateCatalog`, `ManagedRuntimeWarmupService`, `IDialogService`, `ISnackbar`, `ILocalStorageService`
- **Key UI sections:**
  - Setup wizard (`MudPaper`) — shown when `ShowSetupWizard` is true; five sequential steps:
    1. Create config template (opens `ConfigsPageDialog` full-screen).
    2. Import world template (opens `WorldTemplatesPageDialog` full-screen).
    3. Create server (opens `ServersPageDialog` full-screen).
    4. Start server (inline start buttons for each startable server).
    5. Wait for Quasar.Agent (live status rows for running servers).
    Includes `MudProgressLinear`, Back/Skip controls, and a Hide Wizard button.
  - Problem banner (`MudAlert`) — first unhealthy/crashed/faulted or warning instance message.
  - Runtime warmup alert — displays `ManagedRuntimeWarmupService` state and message.
  - KPI summary grid (4 `MudPaper` cards) — online servers, players online, health warnings (yellow tint if > 0), errors (red tint if > 0).
  - `ServerCard` grid — one `<ServerCard>` per instance, passing runtime snapshot and connected agent; callbacks for `StartRequested`, `StopRequested`, `RestartRequested`.
- **Setup-wizard state (local storage keys):**
  - `quasar.dashboard.setupWizardDismissed` — persisted bool; suppresses wizard after Hide is clicked.
  - `quasar.dashboard.setupWizardCompleted` — persisted bool; prevents wizard from reappearing after a restart when setup was previously done.
- **Key computed properties:** `CurrentSetupStep` (0–4, first incomplete or forced override), `SetupProgressPercent`, `ShowSetupWizard`, `OnlineServerCount`, `PlayersOnline`, `WarningServerCount`, `UnhealthyServerCount`, `ProblemBanner`.
- **Key methods:**
  - `StartAsync` / `StopAsync` / `RestartAsync` — delegate to `DedicatedServerSupervisor.SetGoalStateAsync` / `RestartServerAsync`.
  - `OpenCreateConfigProfileDialogAsync` / `OpenImportWorldTemplateDialogAsync` / `OpenCreateServerDialogAsync` — open full-screen `ShowFullScreenPageDialogAsync<TDialog>` dialogs.
  - `PersistSetupCompletionIfNeededAsync` — writes completed flag to local storage once all five steps are satisfied.
  - `HandleRegistryChanged` — invoked on any watched service change; marshals to Blazor thread, persists completion if needed, triggers re-render.
- **Event subscriptions:** `AgentRegistry.Changed`, `DedicatedServerCatalog.Changed`, `DedicatedServerSupervisor.Changed`, `QuasarConfigProfileCatalog.Changed`, `QuasarWorldTemplateCatalog.Changed`, `ManagedRuntimeWarmupService.Changed`.

## Dependencies
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md)
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md)
- [`Quasar/Services/DedicatedServerSupervisor.cs`](../../Services/DedicatedServerSupervisor.cs.md)
- `Quasar/Services/QuasarConfigProfileCatalog.cs`
- `Quasar/Services/QuasarWorldTemplateCatalog.cs`
- [`Quasar/Services/ManagedRuntimeWarmupService.cs`](../../Services/ManagedRuntimeWarmupService.cs.md)
- `Quasar/Components/ServerCard.razor` (child component)
- `Quasar/Components/Pages/ConfigsPageDialog.razor`
- MudBlazor (`MudProgressLinear`, `MudAlert`, `MudGrid`, `MudChip`, `IDialogService`, `ISnackbar`)
- Blazored.LocalStorage (`ILocalStorageService`)

## Notes
- The setup wizard is suppressed permanently once completed (via local storage) and only reappears on explicit "Restart Setup Wizard" button click.
- `ShowSetupWizard` is reactive to runtime changes; as servers start and agents attach the wizard advances steps automatically.
- Local-storage failures during preference read/write are caught and shown as `Severity.Warning` snackbars, not hard failures.
