# Quasar/Components/Pages/Servers.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page at `/servers` managing persistent dedicated-server definitions. Renders a sortable table of `DedicatedServerDefinition`s with live process/agent status, a single lifecycle-action column beside Status, and create/clone/edit/delete/console/world-template actions; expandable rows embed a `ServerDetailPanel` with live agent data. A separate panel lists "unmanaged" agents that report in without a matching server definition.

## Structure
- **`@page "/servers"`**, **`@implements IDisposable`**
- **`[Inject]`:** `DedicatedServerCatalog ServerCatalog`, `DedicatedServerSupervisor Supervisor`, `AgentRegistry Registry`, `QuasarConfigProfileCatalog ConfigProfiles`, `QuasarWorldTemplateCatalog WorldTemplates`, `IDialogService DialogService`, `WebServiceOptions Options`, `ISnackbar Snackbar`, `NavigationManager Navigation`
- **`[Parameter]`:** `EventCallback<string> ConfigProfileSelected` — when set, clicking a config name invokes this instead of navigating to `/configs`.
- **Key UI**
  - Server count chip + "Create Server" button.
  - `MudTable<DedicatedServerDefinition>` with expand toggle and sortable columns: Status (state chip), Actions (Start / Stop / Kill / Restart according to process state), Name (display + unique name), Port, Config (clickable `MudLink` when the profile resolves), Players (`online/max`), Process (PID or "stopped"), Agent (attached state). Row actions at the far right are non-lifecycle controls: Console, Clone, Template, Edit, Delete.
  - `ChildRowContent` renders `<ServerDetailPanel Agent=... />` for expanded rows.
  - Unmanaged Agents `MudPaper` (when any): one `MudExpansionPanel` per connected agent lacking a definition, each containing a `ServerDetailPanel`.
- **State/data:** `_expanded` HashSet drives row expansion; `ServerDefinitions`, `RuntimeSnapshots` (by unique name), `AgentsByUniqueName` (connected, newest by `LastSeenUtc`), `UnmanagedAgents`.
- **Dialog flows**
  - `OpenCreateDialogAsync` / `OpenCloneDialogAsync` / `OpenEditDialogAsync` — open `ServerEditorDialog` (`Definition`, `IsEditing`, `UniqueNameLocked` when editing a running server), then `SaveDefinitionAsync` via `ServerCatalog.UpsertAsync`.
  - `OpenConsoleDialogAsync` — opens `ServerConsoleDialog`.
  - `CreateWorldTemplateAsync` — validates the server is stopped and its world path has `Sandbox.sbc`, opens `WorldTemplateFromServerDialog`, then `WorldTemplates.ImportAsync`.
  - `OpenConfigProfileAsync` — invokes `ConfigProfileSelected` or navigates to `/configs?profileId=...`.
- **Lifecycle controls:** `StartAsync` sets goal On and explicitly starts so `Crashed`/`Faulted` can be operator-retried; `StopAsync` sets goal Off; `KillStartingAsync` calls `Supervisor.KillStartingServerAsync`; `RestartAsync` calls `Supervisor.RestartServerAsync`; `DeleteAsync` confirms (servers must be stopped), then `ServerCatalog.DeleteAsync` + `Registry.PruneDisconnectedByUniqueName`. `Starting` shows Stop and Kill; `Restarting` shows Kill; `Running` shows Stop and Restart; `Stopped`/`Crashed`/`Faulted` show Start.
- **Helpers:** `GetDisplayName`, `GetPlayerCount` (uses configured `MaxPlayers` when available), `GetProcessDisplay`, `GetConfigProfileName`/`CanOpenConfigProfile`, `GetStateText`/`GetStateColor`, `IsRunning`, `CanStartServer`, `CanStopServer`, `CanKillStartingServer`, `CanRestartServer`, `CanCreateWorldTemplate`, `GetAttachmentStatus`, plus sort-value helpers.
- **Blank/clone factories:** `CreateBlank` seeds defaults (port via `AllocateNextPort` from 27016, `ServerIP` 0.0.0.0, health/restart policy fields, health monitoring driven by `Options.DisableServerHealthMonitoring`); `MakeCopyIdentifier` generates a unique `-copy` name; `NormalizeWhitespace`.
- Subscribes to `ServerCatalog`, `Supervisor`, `Registry`, `ConfigProfiles`, `WorldTemplates` `.Changed` in `OnInitialized`, releases in `Dispose`; `HandleChanged` marshals `StateHasChanged`.

## Dependencies
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md) — `DedicatedServerDefinition`, upsert/delete
- [`Quasar/Services/DedicatedServerSupervisor.cs`](../../Services/DedicatedServerSupervisor.cs.md) — goal state, restart, runtime snapshots
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md) — connected agents, prune
- `Quasar/Services/QuasarConfigProfileCatalog.cs`, `Quasar/Services/QuasarWorldTemplateCatalog.cs`
- `Quasar/Services/WebServiceOptions.cs`
- `Quasar/Components/ServerDetailPanel.razor`
- `Quasar/Components/Pages/ServerEditorDialog.razor`, `ServerConsoleDialog.razor`, `WorldTemplateFromServerDialog.razor`
- `Magnetar.Protocol` — process/health/goal state enums, runtime snapshot, `AgentRuntimeState`
- MudBlazor (`MudTable`, `MudExpansionPanel`, `MudChip`, `MudLink`, `IDialogService`, `ISnackbar`)

## Notes
- Renaming a server requires it to be stopped (`SaveDefinitionAsync` blocks renaming a running instance); editing a running server locks the unique name.
- World-template creation requires the server to be fully stopped (goal Off and process Stopped) with a valid world path containing `Sandbox.sbc`.
- Delete archives a deleted JSON snapshot in server history; world files, rendered config, logs and folders remain on disk (not moved to OS trash).
