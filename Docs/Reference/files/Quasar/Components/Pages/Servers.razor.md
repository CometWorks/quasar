# Quasar/Components/Pages/Servers.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page at `/servers` that is the primary control surface for Quasar-managed dedicated servers. Displays an expandable table of all defined servers with live runtime status, and a secondary section for unmanaged agents. Provides Create, Clone, Edit, Delete, Start, Stop, Restart, "Template" (create world template from stopped server), and console-open actions.

## Structure
- **`@page "/servers"`**
- **`@implements IDisposable`**
- **`[Inject]`**
  - `DedicatedServerCatalog ServerCatalog`
  - `DedicatedServerSupervisor Supervisor`
  - `AgentRegistry Registry`
  - `QuasarConfigProfileCatalog ConfigProfiles`
  - `QuasarWorldTemplateCatalog WorldTemplates`
  - `IDialogService DialogService`
  - `WebServiceOptions Options`
  - `ISnackbar Snackbar`
  - `NavigationManager Navigation`
- **`[Parameter]`**
  - `EventCallback<string> ConfigProfileSelected` — raised when user clicks a config profile link; callers can intercept to open a dialog instead of navigating.
- **Key UI**
  - `MudTable<DedicatedServerDefinition>` — sortable columns: expand toggle, Status chip + Start/Stop button, Name (display + unique name), Port, Config (clickable link), Players, Process PID, Agent attachment status, action buttons (Restart, Terminal, Clone, Template, Edit, Delete).
  - Expandable child row — renders `<ServerDetailPanel>` for the selected agent.
  - Unmanaged Agents section — `MudExpansionPanel` per orphaned agent also rendering `<ServerDetailPanel>`.
- **Key methods**
  - `OpenCreateDialogAsync` / `OpenCloneDialogAsync` / `OpenEditDialogAsync` — open `ServerEditorDialog`.
  - `OpenConsoleDialogAsync` — opens `ServerConsoleDialog` for the given `UniqueName`.
  - `CreateWorldTemplateAsync` — validates server is stopped, opens `WorldTemplateFromServerDialog`, then calls `WorldTemplates.ImportAsync`.
  - `StartAsync` / `StopAsync` / `RestartAsync` — delegate to `Supervisor`.
  - `DeleteAsync` — confirms via `ShowMessageBoxAsync`, then `ServerCatalog.DeleteAsync` + `Registry.PruneDisconnectedByUniqueName`.
  - `GetStateColor` / `GetStateText` — map `DedicatedServerProcessState` to MudBlazor `Color` and label string.
  - `AllocateNextPort` — finds the first free port starting at 27016.
  - `MakeCopyIdentifier` — generates a unique `{name}-copy[-N]` identifier.
- **Computed properties**
  - `ServerDefinitions`, `RuntimeSnapshots`, `AgentsByUniqueName`, `UnmanagedAgents`.

## Dependencies
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md)
- [`Quasar/Services/DedicatedServerSupervisor.cs`](../../Services/DedicatedServerSupervisor.cs.md)
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md)
- `Quasar/Services/QuasarConfigProfileCatalog.cs`
- `Quasar/Services/QuasarWorldTemplateCatalog.cs`
- [`Quasar/Components/Pages/ServerEditorDialog.razor`](ServerEditorDialog.razor.md)
- [`Quasar/Components/Pages/ServerConsoleDialog.razor`](ServerConsoleDialog.razor.md)
- `Quasar/Components/Pages/WorldTemplateFromServerDialog.razor`
- `Quasar/Components/Shared/ServerDetailPanel.razor`
- [`Quasar/Models/DedicatedServerDefinition.cs`](../../Models/DedicatedServerDefinition.cs.md)
- `Quasar/Options/WebServiceOptions.cs`
- MudBlazor — `MudTable`, `MudExpansionPanel`, `MudChip`, `MudButton`, `MudIconButton`, `IDialogService`, `ISnackbar`.

## Notes
- The page subscribes to five `Changed` events (ServerCatalog, Supervisor, Registry, ConfigProfiles, WorldTemplates) and calls `InvokeAsync(StateHasChanged)` for each, making it one of the most reactive pages in the UI.
- `CanCreateWorldTemplate` enforces that `GoalState == Off` AND the supervisor runtime state is `Stopped` before enabling the Template button, protecting against snapshotting a partially written world.
- Delete does not move world files to trash; this is noted in the confirmation message text.
