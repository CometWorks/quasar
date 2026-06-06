# Quasar/Components/Pages/Entities.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page (`/entities`) providing a live entity browser for connected Space Engineers server agents. The user selects a connected agent and entity type filter, presses Refresh to fetch up to 500 entities, then can search across the result set and delete individual entities with confirmation. Requires a live Quasar.Agent connection; shows an informational alert otherwise.

## Structure
- **Route:** `@page "/entities"`
- **Implements:** `IDisposable`
- **Injected services:** `AgentRegistry`, `DedicatedServerCatalog`, `EntityService`, `IDialogService`, `ISnackbar`
- **Key UI sections:**
  - Toolbar: server selector `MudSelect` (connected agents only, labelled via `ResolveServerName`), type filter `MudSelect` (All/Grid/Character/Float/Voxel), search `MudTextField`, Refresh button (disabled while loading or no agent selected).
  - Status row: chips for matching count, shown count, total entity count; last-updated timestamp; loading spinner.
  - Conditional alerts for no connected servers, stale agent selection, errors, no results.
  - `MudTable<EntitySummary>` — columns: Type (color-coded chip), Name + EntityId (caption), Sub-type, Blocks, PCU, Size (m), Owner, Position, Delete action; sortable by Name, Blocks, PCU, Size; pager (25/50/100/250 options); fixed header at 60 vh.
- **Key state:** `_selectedAgentId`, `_typeFilter`, `_searchText`, `_entities`, `_lastResult`, `_lastUpdated`, `_loading`, `_error`.
- **Key methods:**
  - `LoadAsync()` — calls `EntityService.GetEntitiesAsync(agent, filter)` with `Limit=500`; client-side `FilteredEntities` then applies the search text.
  - `DeleteEntityAsync(EntitySummary)` — shows `ShowMessageBoxAsync` confirmation, then calls `EntityService.DeleteEntityAsync`; reloads on success.
  - `MatchesSearch(EntitySummary)` — matches against `DisplayName`, `SubType`, `OwnerName`, `EntityId`, `OwnerSteamId`.
  - `HandleChanged()` — re-renders on `AgentRegistry.Changed` / `DedicatedServerCatalog.Changed`; also re-selects a default agent if the current selection was disconnected.
  - `ResolveServerName(AgentRuntimeState agent)` — prefers the server's configured `DedicatedServerDefinition.DisplayName` (looked up by `agent.UniqueNameKey`) over the agent's in-game `ServerDisplayName` (which is `ConfigDedicated.ServerName`, often blank and falling back to "Space Engineers {pid}").
- **Type filter options:** `TypeOptions` static array with values "All", "Grid", "Character", "Float", "Voxel".
- **Event subscriptions:** `AgentRegistry.Changed`, `DedicatedServerCatalog.Changed`.

## Dependencies
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md)
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md) — configured server display names
- [`Quasar/Services/EntityService.cs`](../../Services/EntityService.cs.md)
- `Quasar/Models/EntitySummary.cs`
- `Quasar/Models/EntityListResult.cs`
- `Quasar/Models/EntityListFilter.cs`
- MudBlazor (`MudTable`, `MudSelect`, `MudTextField`, `MudChip`, `IDialogService`, `ISnackbar`)

## Notes
- Entity data is fetched on-demand only (user presses Refresh or the page first renders with a connected agent). There is no auto-refresh to avoid excessive agent load.
- Search filtering is entirely client-side against the fetched batch; the `TypeTag` filter and `Limit=500` are sent to the agent.
