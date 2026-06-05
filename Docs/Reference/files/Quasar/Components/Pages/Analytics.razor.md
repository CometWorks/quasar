# Quasar/Components/Pages/Analytics.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page (`/analytics`) that renders rolling time-series charts for Space Engineers server metrics (SimSpeed, CPU, memory, player count, frame time, PCU, active grids, active entities). Supports multi-server selection, preset and custom time ranges, three data tiers (raw, 1-minute, 1-hour rollup), configurable grid layout, auto-refresh, export trigger, and per-panel layout editing via `AnalyticsPanelDialog`. The full view configuration is persisted in browser local storage under the key `quasar.analytics.view.v2`.

## Structure
- **Route:** `@page "/analytics"`
- **Implements:** `IDisposable`
- **Injected services:** `MetricsStoreService`, `DedicatedServerCatalog`, `AgentRegistry`, `ISnackbar`, `ILocalStorageService`, `IDialogService`
- **Key UI sections:**
  - Toolbar: `MudSelect` for time range (1h/6h/24h/7d/30d/custom), tier mode, auto-refresh interval, point limit; Refresh, Export, Reset layout buttons; Add panel menu for hidden panels.
  - Custom range pickers: `MudDatePicker` + `MudTimePicker` for from/to (shown when range = "custom").
  - Server/Grid settings panel: `MudCheckBox` per discovered server, numeric fields for grid columns, rows, row height.
  - Summary chip row: live aggregate values (SimSpeed, CPU, Memory, Players, PCU, Grids, Entities, Range Avg Sim).
  - Chart grid: CSS grid (`--analytics-grid-columns`, `--analytics-grid-rows`, `--analytics-row-height`) of `MudChart` (ChartType.Timeseries) cards; each card has a settings icon that opens `AnalyticsPanelDialog`.
- **Significant private types:**
  - `MetricDefinition` record — key, title, subtitle, value selector, availability predicate, requires-zero flag.
  - `ChartModel` record — built chart data + layout style + panel ref.
  - `AnalyticsViewConfig` — persisted config (loaded via `ILocalStorageService`).
  - `AnalyticsPanelConfig` — per-panel visibility, order, column/row span.
- **Key methods:**
  - `RefreshView()` — rebuilds server options, normalises selection, queries the right tier, downsamples, builds summary chips and `ChartModel` list.
  - `QueryAutoTier()` — selects raw/1-minute/1-hour tier based on time span.
  - `Downsample()` — uniform stride decimation capped at `PointLimit`.
  - `OpenPanelDialogAsync()` — shows `AnalyticsPanelDialog` and writes back panel settings.
  - `UpdateRefreshTimer()` — creates/disposes a `System.Threading.Timer` for auto-refresh; guarded with `Interlocked.Exchange` to prevent overlapping refreshes.
- **JS interop:** `JSDisconnectedException` is caught during local-storage reads/writes; no direct `IJSRuntime` calls in this file.
- **Event subscriptions:** `AgentRegistry.Changed`, `DedicatedServerCatalog.Changed` — both call `HandleSourceChanged` which marshals to the Blazor thread via `InvokeAsync`.

## Dependencies
- [`Quasar/Services/Analytics/MetricsStoreService.cs`](../../Services/Analytics/MetricsStoreService.cs.md)
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md)
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md)
- `Quasar/Components/Pages/AnalyticsPanelDialog.razor`
- MudBlazor (`MudChart`, `MudSelect`, `MudNumericField`, `MudDatePicker`, `MudTimePicker`, `MudCheckBox`, `MudChip`, `MudMenu`)
- Blazored.LocalStorage (`ILocalStorageService`)

## Notes
- Auto-refresh uses a `System.Threading.Timer` callback that marshals back to Blazor's sync context via `InvokeAsync`. An `Interlocked` flag prevents concurrent refresh calls.
- The chart grid is driven entirely by CSS custom properties injected inline (`ChartGridStyle`); on screens narrower than 1280 px the CSS collapses to a single column (`Analytics.razor.css`).
- Local-storage failures (circuit disconnected, JS errors) are silently swallowed so the page still functions.
