# Quasar/Components/PluginLogPanel.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Reusable panel that displays live plugin log entries from `PluginLogStream`. Provides filter controls for server, plugin, log level, time range, row limit, and free-text search. Subscribes to the stream's `Changed` event to refresh automatically as new entries arrive.

## Structure
No `@page` route — used as a child on the Plugins page.

**Implements:** `IDisposable`

**Injected services:**
- `PluginLogStream LogStream` — in-memory log ring; provides `Query(PluginLogQuery)`, `GetUniqueNames()`, and `GetPlugins()`.
- `AgentRegistry Registry` — resolves live server display names from unique name keys.
- `DedicatedServerCatalog ServerCatalog` — supplies the configured server `DisplayName`, preferred over the live agent name.

**Private state:**
- `_entries` (`IReadOnlyList<PluginLogEntry>`) — current query result.
- `_uniqueNameFilter`, `_pluginFilter`, `_levelFilter`, `_textFilter` — active filter strings.
- `_rangeKey` — one of `"1h"`, `"24h"`, `"7d"`, `"all"` (default `"24h"`).
- `_limit` — integer row cap (default = `PluginLogStream.MaxEntriesPerServer`).

**Filter controls (MudGrid row):**
- Server `MudSelect` — "All" plus `LogStream.GetUniqueNames()` (labels via `ServerName`).
- Plugin `MudSelect` — "All" plus `LogStream.GetPlugins()`.
- Level `MudSelect` — static options: Debug, Info, Warning, Error, Critical.
- Range `MudSelect` — Last 1h / 24h / 7d / All.
- Limit `MudNumericField` — clamped 1 to `PluginLogStream.MaxEntriesPerServer`.
- Text `MudTextField` — immediate free-text filter.

**Log table (`MudTable`):** Fixed header, 420 px height, initially sorted by Time (UTC) descending so the first page shows newest entries; sortable columns for Time (UTC), Level, Server, Plugin; Message column includes optional exception text in `Color.Error`.

**Key methods:**
- `Refresh()` — calls `LogStream.Query(new PluginLogQuery { UniqueName, Plugin, Level, Text, FromUtc, Limit })` with all active filters.
- `ResolveFromUtc()` — maps `_rangeKey` to a `DateTimeOffset?` cutoff.
- `HandleChanged()` — event handler; calls `Refresh()` then `InvokeAsync(StateHasChanged)`.
- `ServerName(uniqueName)` — resolves the configured `DedicatedServerCatalog` `DisplayName` first, then a live agent's `ServerDisplayName`, then the unique name. Logs persist after a server disconnects, so the configured name keeps stale-server rows readable instead of falling back to "Space Engineers {pid}".
- `LevelColor(level)` — maps level string to MudBlazor `Color` enum.

**Static field:** `Levels = ["Debug", "Info", "Warning", "Error", "Critical"]`.

**MudBlazor components used:** `MudPaper`, `MudGrid`, `MudItem`, `MudSelect`, `MudSelectItem`, `MudNumericField`, `MudTextField`, `MudAlert`, `MudTable`, `MudTh`, `MudTd`, `MudText`, `MudTableSortLabel`.

## Dependencies
- [`Quasar/Services/PluginSdk/PluginLogStream.cs`](../Services/PluginSdk/PluginLogStream.cs.md) — log ring buffer with query/filter API
- [`Quasar/Services/AgentRegistry.cs`](../Services/AgentRegistry.cs.md) — agent lookup for live server display names
- [`Quasar/Services/DedicatedServerCatalog.cs`](../Services/DedicatedServerCatalog.cs.md) — configured server display names
- `Magnetar.Protocol.Model.PluginLogEntry`, `PluginLogQuery`
- MudBlazor

## Notes
`HandleChanged` is subscribed to both `PluginLogStream.Changed` and `DedicatedServerCatalog.Changed` (detached in `Dispose`); either event may fire from a background thread, so `InvokeAsync(StateHasChanged)` marshals the re-render back to the Blazor circuit's synchronization context. The limit constant is now `MaxEntriesPerServer` (previously `MaxEntriesPerInstance`).
