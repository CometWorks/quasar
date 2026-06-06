# Quasar/Components/Pages/Plugins.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page at `/plugins` with three sections: a plugin-inventory table aggregated across all connected servers, a live plugin-configuration section that uses a server-selector dropdown to render `PluginConfigEditor`s for one chosen server's plugins, and a `PluginLogPanel` of structured plugin logs. On first render it triggers a background refresh of `QuasarPluginCatalogService` to resolve GitHub source URLs for inventory rows.

## Structure
- **`@page "/plugins"`**, **`@implements IDisposable`**
- **`[Inject]`:** `AgentRegistry Registry`, `DedicatedServerCatalog ServerCatalog`, `PluginConfigService ConfigService`, `QuasarPluginCatalogService PluginCatalog`
- **Key UI**
  - Plugin inventory `MudTable<PluginRow>` — sortable columns Plugin (display name), Version, Server, Host, Status (`loaded`/`declared`), plus an "Open in new" icon button linking to the resolved GitHub repository when known; `NoRecordsContent` info alert.
  - Plugin configuration section — a `MudSelect` "Server" dropdown (options from `ConfigAgents`, labelled via `ResolveServerName`) picks a single server; the selected server's `MudPaper` (resolved server name + host display) holds a `MudExpansionPanel` per plugin wrapping `<PluginConfigEditor AgentId=... Plugin=... />`. Info alert when no agent exposes configs. Replaces the previous layout that stacked a panel for every config-exposing server at once.
  - `<PluginLogPanel />` — structured plugin log display.
- **`PluginRow` (private sealed class)** — `DisplayName`, `Version`, `ServerName`, `HostName`, `IsLoaded`, `SourceUrl`.
- **`PluginRows` computed property** — flattens `agent.Snapshot?.Plugins` across all agents into rows (each row's `ServerName` resolved via `ResolveServerName`), ordered by display name then server name.
- **`ConfigAgents` computed property** — connected agents where `ConfigService.HasConfigs(agent.AgentId)`; backs both the empty-state check and the server dropdown.
- **`_selectedConfigAgentId`** — the dropdown's currently selected agent id (nullable).
- **`GetSelectedConfigAgent()`** — returns the `ConfigAgents` entry matching `_selectedConfigAgentId`, falling back to the first one (so a server's plugins always show even before the user picks one, or after the selected server disconnects); null when none expose configs.
- **`HandleConfigAgentChanged(string agentId)`** — stores the dropdown selection into `_selectedConfigAgentId`.
- **`ResolveServerName(AgentRuntimeState agent)`** — prefers the server's configured `DedicatedServerDefinition.DisplayName` (looked up by `agent.UniqueNameKey`) over the agent's in-game `ServerDisplayName`; drives both the inventory `ServerName` and the config dropdown labels.
- **`GetPluginSourceUrl`** — matches a `PluginRuntimeInfo` against `PluginCatalog.GetEntries()` by `PluginId`, `DisplayName`, or filename-stem (`IsPluginMatch` compares against entry `PluginId`/`FriendlyName`), then returns `QuasarPluginCatalogService.GetRepositoryUrl(entry.SourceRepo)`.
- **`OnAfterRenderAsync`** — on first render, if the catalog is empty, awaits `PluginCatalog.RefreshAsync()` and re-renders; exceptions are silently swallowed.
- Subscribes to `Registry.Changed`, `ServerCatalog.Changed`, and `ConfigService.Changed` in `OnInitialized`, releases in `Dispose`; `HandleRegistryChanged` marshals `StateHasChanged`.

## Dependencies
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md) — agents, snapshots, `AgentRuntimeState`
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md) — configured server display names
- `Quasar/Services/PluginConfigService.cs` — per-agent config availability/editing
- [`Quasar/Services/QuasarPluginCatalogService.cs`](../../Services/QuasarPluginCatalogService.cs.md) — catalog entries, repo URL resolution, `QuasarPluginCatalogEntry`
- `Quasar/Components/Shared/PluginConfigEditor.razor`
- `Quasar/Components/Shared/PluginLogPanel.razor`
- `Magnetar.Protocol` — `PluginRuntimeInfo`
- MudBlazor — `MudTable`, `MudSelect`, `MudExpansionPanels`, `MudIconButton`, `MudAlert`, `MudPaper`
