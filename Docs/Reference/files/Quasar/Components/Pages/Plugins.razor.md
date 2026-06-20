# Quasar/Components/Pages/Plugins.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page at `/plugins` with live plugin configuration and structured plugin logs. The configuration section uses a server-selector dropdown to render `PluginConfigEditor`s for one chosen server's plugins, and `PluginLogPanel` shows PluginSdk log output captured from managed dedicated servers.

## Structure
- **`@page "/plugins"`**, **`@implements IDisposable`**
- **`[Inject]`:** `AgentRegistry Registry`, `DedicatedServerCatalog ServerCatalog`, `PluginConfigService ConfigService`
- **Key UI**
  - Plugin configuration section — a `MudSelect` "Server" dropdown (options from `ConfigAgents`, labelled via `ResolveServerName`) picks a single server; the selected server's `MudPaper` (resolved server name + host display) holds a `MudExpansionPanel` per plugin wrapping `<PluginConfigEditor AgentId=... Plugin=... />`. Info alert when no agent exposes configs. Replaces the previous layout that stacked a panel for every config-exposing server at once.
  - `<PluginLogPanel />` — structured plugin log display.
- **`ConfigAgents` computed property** — connected agents where `ConfigService.HasConfigs(agent.AgentId)`; backs both the empty-state check and the server dropdown.
- **`_selectedConfigAgentId`** — the dropdown's currently selected agent id (nullable).
- **`GetSelectedConfigAgent()`** — returns the `ConfigAgents` entry matching `_selectedConfigAgentId`, falling back to the first one (so a server's plugins always show even before the user picks one, or after the selected server disconnects); null when none expose configs.
- **`HandleConfigAgentChanged(string agentId)`** — stores the dropdown selection into `_selectedConfigAgentId`.
- **`ResolveServerName(AgentRuntimeState agent)`** — prefers the server's configured `DedicatedServerDefinition.DisplayName` (looked up by `agent.UniqueNameKey`) over the agent's in-game `ServerDisplayName`; drives the config dropdown labels.
- Subscribes to `Registry.Changed`, `ServerCatalog.Changed`, and `ConfigService.Changed` in `OnInitialized`, releases in `Dispose`; `HandleRegistryChanged` marshals `StateHasChanged`.

## Dependencies
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md) — agents, snapshots, `AgentRuntimeState`
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md) — configured server display names
- [`Quasar/Services/PluginSdk/PluginConfigService.cs`](../../Services/PluginSdk/PluginConfigService.cs.md) — per-agent config availability/editing
- `Quasar/Components/Shared/PluginConfigEditor.razor`
- `Quasar/Components/Shared/PluginLogPanel.razor`
- MudBlazor — `MudSelect`, `MudExpansionPanels`, `MudAlert`, `MudPaper`
