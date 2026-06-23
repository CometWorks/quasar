# Quasar/Components/Pages/Entities.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page (`/entities`) providing a live entity browser for connected Space Engineers server agents. The user selects a connected agent and entity type filter, presses Refresh to fetch up to 500 entities, then can search across the result set and delete individual entities or a multi-selected batch with confirmation. Entity rows support normal click selection, Ctrl/Meta toggle selection, and Shift range selection. Requires a live Quasar.Agent connection; shows an informational alert otherwise.

## Structure
- **Route:** `@page "/entities"`
- **Implements:** `IDisposable`
- **Injected services:** `AgentRegistry`, `DedicatedServerCatalog`, `EntityService`, `IDialogService`, `ISnackbar`
- **Key UI sections:**
  - Toolbar: server selector `MudSelect` (connected agents only, labelled via `ResolveServerName`), type filter `MudSelect` (All/Grid/Character/Float/Voxel), search `MudTextField`, Refresh button (disabled while loading or no agent selected).
  - Status/action row: chips for matching count, shown count, total entity count, selected count; last-updated timestamp; loading spinner; multi-selection actions for Select shown, Clear, and Delete selected.
  - Conditional alerts for no connected servers, stale agent selection, errors, no results.
  - `MudTable<EntitySummary>` — columns show Type, Entity ID, Sub-type, Blocks, PCU, Size (m), Owner, Position, Name, and a rightmost unlabeled Delete action column; sortable by Name, Blocks, PCU, Size; row click selection via `OnRowClick`; selected rows get the shared `quasar-row-selected` hover-palette highlight; pager (25/50/100/250 options); fixed header at 60 vh.
- **Key state:** `_selectedAgentId`, `_typeFilter`, `_searchText`, `_entities`, `_selectedEntityIds`, `_entityTable`, `_lastResult`, `_lastUpdated`, `_selectionAnchorEntityId`, `_loading`, `_deletingSelected`, `_error`.
- **Key methods:**
  - `LoadAsync()` — calls `EntityService.GetEntitiesAsync(agent, filter)` with `Limit=500`; client-side `FilteredEntities` then applies the search text.
  - `DeleteEntityAsync(EntitySummary)` — shows `ShowMessageBoxAsync` confirmation, then calls `EntityService.DeleteEntityAsync`; reloads on success.
  - `DeleteSelectedEntitiesAsync()` — confirms a selected batch, deletes each entity through `EntityService.DeleteEntityAsync`, reports success/failure counts, clears selection on successful deletes, then reloads.
  - `HandleRowClick(TableRowClickEventArgs<EntitySummary>)` — applies desktop-style selection semantics: plain click selects one row, Ctrl/Meta toggles a row, Shift selects a visible filtered range from `_selectionAnchorEntityId`.
  - `SelectFilteredEntities()` / `ClearSelection()` — toolbar actions for bulk-selecting all currently filtered rows or clearing the current selection.
  - `GetSelectionOrder()` — prefers the MudTable `FilteredItems` order for Shift ranges, so range selection tracks the table's current sorted/filtered view when available.
  - `SelectRange(long, long, bool)`, `ToggleSelection(long)`, `PruneSelectionToLoadedEntities()`, `PruneSelectionToFilteredEntities()` — helpers that keep selection keyed by `EntityId` and remove hidden/stale selected IDs when the loaded result set or search text changes.
  - `OnSearchChanged(string)` — updates the client-side search text and prunes selection to the visible filtered list.
  - `GetEntityRowClass(EntitySummary, int)` — marks all rows selectable and selected rows with `quasar-row-selected`.
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
- Selection is intentionally scoped to the current loaded batch. It is cleared on server/type changes and refreshes, and pruned when the search text hides selected rows, so bulk delete does not target invisible stale rows.
