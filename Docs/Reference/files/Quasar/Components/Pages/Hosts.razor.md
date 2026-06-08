# Quasar/Components/Pages/Hosts.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Source-only host summary component. It is intentionally not routable for now, so direct navigation to `/hosts` falls through to the app Not Found page. When enabled again, it shows a summary table of every host (physical or virtual machine) that has connected at least one Quasar.Agent. Rows are aggregated from `AgentRegistry` by `HostKey`, displaying the host display name, how many distinct server slots are running on it, how many of those agents are currently connected, and total players online across that host.

## Structure
- No `@page` route while the hosts page is hidden.
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
- The Hosts navigation link is currently hidden, and `/hosts` is intentionally unavailable.
- This page was previously named `Nodes.razor` (route `/nodes`); it is now `Hosts.razor` (route `/hosts`).
