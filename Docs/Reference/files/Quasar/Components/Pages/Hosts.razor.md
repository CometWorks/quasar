# Quasar/Components/Pages/Hosts.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page at `/hosts` showing a summary table of every host (physical or virtual machine) that has connected at least one Quasar.Agent. Rows are aggregated from `AgentRegistry` by `HostKey`, displaying the host display name, how many distinct server slots are running on it, how many of those agents are currently connected, and total players online across that host.

## Structure
- **`@page "/hosts"`**
- **`@implements IDisposable`**
- **`[Inject]`**
  - `AgentRegistry Registry`
- **Key UI**
  - `MudTable<HostRow>` — sortable columns: Host (display name), Servers (distinct server count), Connected Agents, Players. Shows "No host data yet." alert when empty.
- **`HostRow` (private sealed class)** — `HostName`, `ServerCount`, `ConnectedAgents`, `PlayersOnline`.
- **`HostRows` computed property** — groups `Registry.GetAgents()` by `HostKey` (case-insensitive), then projects each group to a `HostRow`, ordered by `HostName`.
- **Event subscription:** `Registry.Changed` → `HandleRegistryChanged` → `InvokeAsync(StateHasChanged)`.

## Dependencies
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md)
- MudBlazor — `MudTable`, `MudTableSortLabel`, `MudAlert`.

## Notes
- No `[Parameter]`s; entirely driven by live `AgentRegistry` state.
- This page was previously named `Nodes.razor` (route `/nodes`); it is now `Hosts.razor` (route `/hosts`).
