# Quasar/Components/Pages/Plugins.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page at `/plugins` with three sections: a plugin inventory table across all connected servers, a live plugin configuration section that renders `PluginConfigEditor` for each connected agent with exposed configs, and a `PluginLogPanel` showing structured plugin log output. On first render it triggers a background refresh of the `QuasarPluginCatalogService` to resolve GitHub source URLs.

## Structure
- **`@page "/plugins"`**
- **`@implements IDisposable`**
- **`[Inject]`**
  - `AgentRegistry Registry`
  - `PluginConfigService ConfigService`
  - `QuasarPluginCatalogService PluginCatalog`
- **Key UI**
  - `MudTable<PluginRow>` — columns: Plugin (display name), Version, Server, Node, Status (loaded/declared), external link button to GitHub repo if known.
  - Plugin configuration section — iterates connected agents that `ConfigService.HasConfigs(agentId)`, renders a `MudExpansionPanel` per plugin containing `<PluginConfigEditor>`.
  - `<PluginLogPanel />` — reusable log display component.
- **`PluginRow` (private sealed class)** — `DisplayName`, `Version`, `ServerName`, `NodeName`, `IsLoaded`, `SourceUrl`.
- **`PluginRows` computed property** — flattens `agent.Snapshot?.Plugins` across all agents.
- **`GetPluginSourceUrl`** — matches `PluginRuntimeInfo` against `QuasarPluginCatalogService.GetEntries()` by `PluginId`, `DisplayName`, or filename-stem; returns the GitHub repository URL.
- **`OnAfterRenderAsync`** — calls `PluginCatalog.RefreshAsync()` on first render if catalog is empty; errors are silently swallowed.

## Dependencies
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md)
- `Quasar/Services/PluginConfigService.cs`
- [`Quasar/Services/QuasarPluginCatalogService.cs`](../../Services/QuasarPluginCatalogService.cs.md)
- `Quasar/Components/Shared/PluginConfigEditor.razor`
- `Quasar/Components/Shared/PluginLogPanel.razor`
- `Magnetar.Protocol` — `PluginRuntimeInfo`.
- MudBlazor — `MudTable`, `MudExpansionPanels`, `MudIconButton`, `MudAlert`, `MudPaper`.
