# Quasar/Components/Pages/Analytics.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page (`/analytics`) that renders rolling, interruptible ApexCharts time-series charts for Space Engineers server metrics (SimSpeed, CPU, memory, player count, frame time, PCU, active grids, active entities). Supports multi-server selection, preset and custom time ranges, configurable grid layout, auto-refresh, export trigger, and per-panel layout editing via `AnalyticsPanelDialog`. The full view configuration is persisted in browser local storage under the key `quasar.analytics.view.v2`.

## Structure
- **Route:** `@page "/analytics"`
- **Implements:** `IDisposable`
- **Injected services:** `MetricsStoreService`, `DedicatedServerCatalog`, `AgentRegistry`, `ISnackbar`, `ILocalStorageService`, `IDialogService`, `ThemePreferenceService`, `IJSRuntime`
- **Key UI sections:**
- Toolbar: `MudSelect` for time range (30s/1m/2m/5m/15m/30m/1h/6h/24h/7d/30d/custom), auto-refresh interval; Refresh, Export, Reset layout buttons; Add panel menu for hidden panels.
  - Custom range pickers: `MudDatePicker` + `MudTimePicker` for from/to (shown when range = "custom").
  - Server/Grid settings panel: `MudCheckBox` per discovered server, numeric fields for grid columns, rows, row height, and line capping controls (`Max visible lines`, `Show all selected server lines`).
  - Summary chip row: live aggregate values (SimSpeed, CPU, Memory, Players, PCU, Grids, Entities, Range Avg Sim).
  - Chart grid: CSS grid (`--analytics-grid-columns`, `--analytics-grid-rows`, `--analytics-row-height`) of ApexCharts line-chart cards; each card has a settings icon that opens `AnalyticsPanelDialog`.
- **Significant private types:**
  - `MetricDefinition` record — key, title, subtitle, value selector, availability predicate, requires-zero flag.
  - `ChartModel` record — built chart series, ApexCharts options, layout style, and panel ref.
  - `AnalyticsChartSeries` / `AnalyticsChartPoint` records — per-server line-series data with nullable Y values so missing samples and explicit gaps interrupt the rendered line.
  - `AnalyticsViewConfig` — persisted config (loaded via `ILocalStorageService`).
  - `AnalyticsPanelConfig` — per-panel visibility, order, column/row span.
- **Key methods:**
- `RefreshViewAsync()` — rebuilds server options, normalises selection, resolves time range and viewport-derived point budget, loads sampled data, down-samples and caches transformed points, and builds summary chips plus chart models.
  - `BuildSeriesPoints()` — sorts metric samples by timestamp, inserts null-valued gap points when sample spacing exceeds the disruption threshold, and emits null values for unavailable/non-finite metric samples.
- `CreateChartOptions()` — configures ApexCharts line rendering, a blue-first series palette, datetime axes fixed to the selected time window (keeps moving scale), tooltip formatting, markers, legends, null-point behaviour, fixed 0..100 ms Y-axis for frame-time, and theme mode from `ThemePreferenceService` (light/dark).
  - `ResolveChartHeight()` — derives an explicit pixel chart height from the panel row span and configured row height so ApexCharts does not collapse inside the flex card.
- `ResolveGapThresholdSeconds()` — uses observed sample cadence and a raw baseline to detect missing periods and insert interruption points without false breaks.
- `DownsampleToPointLimit()` — chunk-based averaging decimation to keep each series within budget; trailing leftover samples are intentionally dropped when they form a partial chunk, matching the dashboard averaging policy.
  - point budget is resolved per chart by viewport width (`chartPxWidth * 1.5`), capped by `[300, 1000]`, then divided by visible server count to keep total chart points bounded.
  - `OpenPanelDialogAsync()` — shows `AnalyticsPanelDialog` and writes back panel settings.
  - `UpdateRefreshTimer()` — creates/disposes a `System.Threading.Timer` for auto-refresh; guarded with `Interlocked.Exchange` to prevent overlapping refreshes.
- **JS interop:** `IJSRuntime` is used for viewport width via `quasarConfigs.getViewportWidth` for dynamic chart point budgets; `JSDisconnectedException` is still handled for local-storage operations and width lookup fallbacks.
- **Event subscriptions:** `AgentRegistry.Changed`, `DedicatedServerCatalog.Changed` — both call `HandleSourceChanged` which marshals to the Blazor thread via `InvokeAsync`.

## Dependencies
- [`Quasar/Services/Analytics/MetricsStoreService.cs`](../../Services/Analytics/MetricsStoreService.cs.md)
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md)
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md)
- `Quasar/Components/Pages/AnalyticsPanelDialog.razor`
- ApexCharts (`ApexChart`, `ApexPointSeries`, `ApexChartOptions`)
- MudBlazor (`MudSelect`, `MudNumericField`, `MudDatePicker`, `MudTimePicker`, `MudCheckBox`, `MudChip`, `MudMenu`, `MudButton`)
- Blazored.LocalStorage (`ILocalStorageService`)

## Notes
- Auto-refresh uses a `System.Threading.Timer` callback that marshals back to Blazor's sync context via `InvokeAsync`. An `Interlocked` flag prevents concurrent refresh calls.
- ApexCharts receives nullable decimal Y values and `ShowNullDataPoints = false`; null points create visible interruptions instead of connecting across bad or missing metric data. Gap detection is downsample-aware, so a low point limit alone does not split otherwise continuous data.
- Frame time is always rendered on a 0..100 ms Y-axis to keep rare outliers from flattening normal samples against the bottom of the chart.
- Per-chart point budget scales with chart width and visible lines: the same selected time range can be displayed with more detail on wider charts and fewer points when many server lines are visible.
- Large server selections can be capped in-page via `Max visible lines` to control concurrent line rendering, with a `Show all selected server lines` override.
- ApexCharts axis titles are intentionally omitted because the surrounding panel already shows the metric title/subtitle.
- The chart grid is driven entirely by CSS custom properties injected inline (`ChartGridStyle`); on screens narrower than 1280 px the CSS collapses to a single column (`Analytics.razor.css`).
- Local-storage failures (circuit disconnected, JS errors) are silently swallowed so the page still functions.
