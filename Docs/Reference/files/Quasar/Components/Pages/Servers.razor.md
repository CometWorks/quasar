# Quasar/Components/Pages/Servers.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Embeddable server-management component used by the dashboard list view and full-screen server dialog. Renders a sortable table of `DedicatedServerDefinition`s with live process/agent status, a single lifecycle-action column beside Status, and create/clone/edit/delete/console/world-template actions; destructive Stop/Kill/Delete actions require confirmation. Clone asks whether to copy world state or leave the clone without a world, always clears path overrides so the clone gets independent DS/config/world paths, and refuses explicit path reuse. Expanded rows embed a `ServerDetailPanel` with the server definition, runtime snapshot, and live agent data. A separate panel lists "unmanaged" agents that report in without a matching server definition.

## Structure
- **No `@page` route**; **`@implements IDisposable`**. `Home.razor` embeds this component for the dashboard list view selected by `?view=list`.
- **`[Inject]`:** `DedicatedServerCatalog ServerCatalog`, `DedicatedServerSupervisor Supervisor`, `AgentRegistry Registry`, `QuasarConfigProfileCatalog ConfigProfiles`, `QuasarWorldTemplateCatalog WorldTemplates`, `IDialogService DialogService`, `WebServiceOptions Options`, `ISnackbar Snackbar`, `NavigationManager Navigation`
- **`[Parameter]`:** `EventCallback<string> ConfigProfileSelected` — when set, clicking a config name invokes this instead of navigating to `/configs`.
- **`[Parameter]`:** `Embedded` — suppresses the page title when the component is hosted inside another page.
- **`[Parameter]`:** `HideHeader` — suppresses the top title/description copy for embedded use.
- **`[Parameter]`:** `DisableResponsiveLayout` — sets the `MudTable` breakpoint to `Breakpoint.None`, keeping the table layout instead of MudBlazor's responsive card/list rendering.
- **`[Parameter]`:** `LaunchBlocked`, `LaunchBlockedMessage` — disables/guards Start buttons while the managed runtime is still preparing and shows the supplied warning message.
- **Key UI**
  - Server count chip + "Create Server" button.
  - `MudTable<DedicatedServerDefinition>` with `Class="servers-list-table"`, expand toggle, and sortable columns: Status (state chip), Actions (Start / Stop / Kill / Restart according to process state), Name (display + unique name), Port, Config (clickable `MudLink` when the profile resolves), Players (`online/max`), Process (PID or "stopped"), Agent (attached state). Row actions at the far right are non-lifecycle controls: Console, Clone, Template, Edit, Delete.
  - `ChildRowContent` renders `<ServerDetailPanel Server=... Runtime=... Agent=... />` for expanded rows inside a `servers-list-detail-row`, which opts the detail area out of the global table-row hover inversion while allowing nested tables to keep their own row hover behavior.
  - Unmanaged Agents `MudPaper` (when any): one `MudExpansionPanel` per connected agent lacking a definition, each containing a `ServerDetailPanel`.
- **State/data:** `_expanded` HashSet drives row expansion; `ServerDefinitions`, `RuntimeSnapshots` (by unique name), `AgentsByUniqueName` (connected, newest by `LastSeenUtc`), `UnmanagedAgents`.
- **Dialog flows**
  - `OpenCreateDialogAsync` / `OpenEditDialogAsync` — open `ServerEditorDialog` (`Definition`, `IsEditing`, `UniqueNameLocked` when editing a running server), then `SaveDefinitionAsync` via `ServerCatalog.UpsertAsync`.
  - `OpenCloneDialogAsync` — clones the definition with a new identifier, port, goal Off, autostart disabled, and blank path overrides so catalog normalization assigns fresh managed paths. Opens `ServerEditorDialog` with clone mode labels, then asks for `Copy World` or `No World`: stopped sources copy the current world folder (skipping `Sandbox_config.sbc*`), running sources copy the newest Space Engineers `Backup/` snapshot, and no-world clones delete any existing target world folder so first start seeds from the selected template. If the admin manually reuses the source DS app-data, world, or rendered config path, clone is refused.
  - `OpenConsoleDialogAsync` — opens `ServerConsoleDialog`.
  - `CreateWorldTemplateAsync` — validates the server is stopped and its world path has `Sandbox.sbc`, opens `WorldTemplateFromServerDialog`, then `WorldTemplates.ImportAsync`.
  - `OpenConfigProfileAsync` — invokes `ConfigProfileSelected` or navigates to `/configs?profileId=...`.
- **Lifecycle controls:** `StartAsync` first respects `LaunchBlocked`, then sets goal On and explicitly starts so `Crashed`/`Faulted` can be operator-retried; `StopAsync` confirms then sets goal Off; `KillStartingAsync` confirms then calls `Supervisor.KillStartingServerAsync`; `RestartAsync` calls `Supervisor.RestartServerAsync`; `DeleteAsync` confirms (servers must be stopped), then `ServerCatalog.DeleteAsync` + `Registry.PruneDisconnectedByUniqueName`. `Starting` shows Stop and Kill; `Restarting` shows Kill; `Running` shows Stop and Restart; `Stopped`/`Crashed`/`Faulted` show Start.
- **Helpers:** `GetDisplayName`, `GetPlayerCount` (uses configured `MaxPlayers` when available), `GetProcessDisplay`, `GetConfigProfileName`/`CanOpenConfigProfile`, `GetStateText`/`GetStateColor`, `IsRunning`, `CanStartServer`, `CanStopServer`, `CanKillStartingServer`, `CanRestartServer`, `CanCreateWorldTemplate`, `GetAttachmentStatus`, plus sort-value helpers.
- **Blank/clone factories/helpers:** `CreateBlank` seeds defaults (port via `AllocateNextPort` from 27016, `ServerIP` 0.0.0.0, health/restart policy fields, health monitoring driven by `Options.DisableServerHealthMonitoring`); `MakeCopyIdentifier` generates a unique `-copy` name; clone helpers choose world mode, guard independent paths, find latest SE `Backup/` snapshots, copy directories, and compare normalized paths; `NormalizeWhitespace`.
- Subscribes to `ServerCatalog`, `Supervisor`, `Registry`, `ConfigProfiles`, `WorldTemplates` `.Changed` in `OnInitialized`, releases in `Dispose`; `HandleChanged` marshals `StateHasChanged`.

## Dependencies
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md) — `DedicatedServerDefinition`, upsert/delete
- [`Quasar/Services/DedicatedServerSupervisor.cs`](../../Services/DedicatedServerSupervisor.cs.md) — goal state, restart, runtime snapshots
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md) — connected agents, prune
- `Quasar/Services/QuasarConfigProfileCatalog.cs`, `Quasar/Services/QuasarWorldTemplateCatalog.cs`
- `Quasar/Services/WebServiceOptions.cs`
- `Quasar/Components/ServerDetailPanel.razor`
- `Quasar/Components/Pages/ServerEditorDialog.razor`, `ServerConsoleDialog.razor`, `WorldTemplateFromServerDialog.razor`
- [`Quasar/Components/Pages/Home.razor`](Home.razor.md) — embeds the list view with `DisableResponsiveLayout=true`.
- [`Quasar/wwwroot/app.css`](../../wwwroot/app.css.md) — `.servers-list-table` horizontal-scroll/min-width styling.
- `Magnetar.Protocol` — process/health/goal state enums, runtime snapshot, `AgentRuntimeState`
- MudBlazor (`MudTable`, `MudExpansionPanel`, `MudChip`, `MudLink`, `IDialogService`, `ISnackbar`)

## Notes
- Renaming a server requires it to be stopped (`SaveDefinitionAsync` blocks renaming a running instance); editing a running server locks the unique name.
- World-template creation requires the server to be fully stopped (goal Off and process Stopped) with a valid world path containing `Sandbox.sbc`.
- Delete archives a deleted JSON snapshot in server history; world files, rendered config, logs and folders remain on disk (not moved to OS trash).
- Running-source world clone requires an existing Space Engineers `Backup/` snapshot under the source world path; Quasar does not copy the live world directory while the server is active.
