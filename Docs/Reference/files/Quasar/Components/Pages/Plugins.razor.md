# Quasar/Components/Pages/Plugins.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page at `/plugins` with three sections: a plugin-inventory table aggregated across all connected servers, a live plugin-configuration section that renders a `PluginConfigEditor` per plugin for each connected agent exposing configs, and a `PluginLogPanel` of structured plugin logs. On first render it triggers a background refresh of `QuasarPluginCatalogService` to resolve GitHub source URLs for inventory rows.

## Structure
- **`@page "/plugins"`**, **`@implements IDisposable`**
- **`[Inject]`:** `AgentRegistry Registry`, `PluginConfigService ConfigService`, `QuasarPluginCatalogService PluginCatalog`
- **Key UI**
  - Plugin inventory `MudTable<PluginRow>` — sortable columns Plugin (display name), Version, Server, Host, Status (`loaded`/`declared`), plus an "Open in new" icon button linking to the resolved GitHub repository when known; `NoRecordsContent` info alert.
  - Plugin configuration section — iterates connected agents where `ConfigService.HasConfigs(agent.AgentId)`; per agent a `MudPaper` (server display + host display) with a `MudExpansionPanel` per plugin wrapping `<PluginConfigEditor AgentId=... Plugin=... />`. Info alert when no agent exposes configs.
  - `<PluginLogPanel />` — structured plugin log display.
- **`PluginRow` (private sealed class)** — `DisplayName`, `Version`, `ServerName`, `HostName`, `IsLoaded`, `SourceUrl`.
- **`PluginRows` computed property** — flattens `agent.Snapshot?.Plugins` across all agents into rows, ordered by display name then server name.
- **`GetPluginSourceUrl`** — matches a `PluginRuntimeInfo` against `PluginCatalog.GetEntries()` by `PluginId`, `DisplayName`, or filename-stem (`IsPluginMatch` compares against entry `PluginId`/`FriendlyName`), then returns `QuasarPluginCatalogService.GetRepositoryUrl(entry.SourceRepo)`.
- **`OnAfterRenderAsync`** — on first render, if the catalog is empty, awaits `PluginCatalog.RefreshAsync()` and re-renders; exceptions are silently swallowed.
- Subscribes to `Registry.Changed` and `ConfigService.Changed` in `OnInitialized`, releases in `Dispose`; `HandleRegistryChanged` marshals `StateHasChanged`.

## Dependencies
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md) — agents, snapshots
- `Quasar/Services/PluginConfigService.cs` — per-agent config availability/editing
- [`Quasar/Services/QuasarPluginCatalogService.cs`](../../Services/QuasarPluginCatalogService.cs.md) — catalog entries, repo URL resolution, `QuasarPluginCatalogEntry`
- `Quasar/Components/Shared/PluginConfigEditor.razor`
- `Quasar/Components/Shared/PluginLogPanel.razor`
- `Magnetar.Protocol` — `PluginRuntimeInfo`
- MudBlazor — `MudTable`, `MudExpansionPanels`, `MudIconButton`, `MudAlert`, `MudPaper`
