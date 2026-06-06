# Quasar/Components/Pages/Analytics.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Interactive `/analytics` dashboard that renders rolling Space Engineers server metrics as ApexCharts time-series line charts. It reads downsampled samples from `MetricsStoreService`, supports per-server selection, a configurable CSS-grid panel layout, persisted view config (localStorage), auto-refresh, custom time ranges, theme-aware chart styling, and a per-panel settings dialog. The whole charting stack was migrated to ApexCharts with new summary chips, an "Add panel" menu, and auto-refresh.

## Structure
- `@page "/analytics"`, `@implements IDisposable`.
- **`[Inject]`ed services:** `MetricsStoreService MetricsStore`, `DedicatedServerCatalog ServerCatalog`, `AgentRegistry Registry`, `ISnackbar Snackbar`, `ILocalStorageService LocalStorage`, `IDialogService DialogService`, `ThemePreferenceService ThemePreference`, `IJSRuntime JS`. No `[Parameter]`s.
- **Toolbar:** time-range `MudSelect` (30s..30d + Custom), auto-refresh `MudSelect` (Off/5/15/30/60s), Refresh / Export / Reset-layout buttons, and an "Add panel" `MudMenu` listing hidden panels. Custom range shows date/time pickers.
- **Filters paper:** server checkbox grid with "Select all"; grid controls (`Columns`, `Rows`, `Row height`, `Max visible lines`, "Show all selected server lines") plus "Reset panels".
- **Summary chips row:** Servers, SimSpeed, CPU, Memory, Players, PCU, Grids, Entities, Range Avg Sim.
- **Chart grid:** CSS-grid (`analytics-chart-grid`) of `MudPaper` cards, each an `ApexChart<AnalyticsChartPoint>` with `ApexPointSeries` (one Line series per server) and a "Tune" `MudIconButton` opening `AnalyticsPanelDialog`.
- **Metrics (`MetricDefinitions`):** simspeed, cpu, memory (GB), players, frametime, pcu, grids, entities — each with selector/availability/RequiresZero.
- **Refresh pipeline:** `RefreshViewAsync` resolves range/servers, picks raw/1-minute/1-hour rollup tier by span, downsamples to a per-series point budget derived from viewport width (`quasarConfigs.getViewportWidth` JS interop), builds series with gap-insertion, and either remounts charts (keyed by render version) or updates existing charts in place via `UpdateOptionsAsync`/`UpdateSeriesAsync`/`ZoomXAsync`. Source-change events are coalesced through `ProcessQueuedRefreshAsync`; auto-refresh runs a `PeriodicTimer` loop.
- **Persistence/config:** `AnalyticsViewConfig` (storage key `quasar.analytics.view.v2`) with `NormalizeConfig`, `CreateDefaultPanels`, panel visibility/order/spans.
- **Chart options:** `CreateChartOptions` builds theme-aware (`_isDarkTheme`) `ApexChartOptions` — datetime X axis, formatted labels/tooltips by span, per-metric Y-axis decimals/max/formatter, dense-chart marker suppression.
- **Helper records/classes:** `ServerOption`, `SummaryChipModel`, `MetricDefinition`, `ChartModel`, `AnalyticsChartSeries`, `AnalyticsChartPoint`, `SeriesTransformCacheKey`.

## Dependencies
- `Quasar/Components/Pages/AnalyticsPanelDialog.razor` (panel settings dialog)
- `Quasar/Services/MetricsStoreService.cs` (and `ServerMetricsStore` / `MetricSample`)
- `Quasar/Services/DedicatedServerCatalog.cs`
- `Quasar/Services/AgentRegistry.cs`
- [`Quasar/Services/ThemePreferenceService.cs`](../../Services/ThemePreferenceService.cs.md)
- [`Quasar/Components/Pages/Analytics.razor.css`](Analytics.razor.css.md) (scoped styles)
- External: **ApexCharts** (`ApexChart`, `ApexPointSeries`, `ApexChartOptions`), **MudBlazor**, **Blazored.LocalStorage** (`ILocalStorageService`), `Microsoft.JSInterop`.

## Notes
- ApexChart instances are held by reference (`ChartRef`) so series/options/zoom can be updated without a full remount; `RenderKey` (`{panel}:{version}`) forces a remount when layout/range changes.
- Chart point budget adapts to viewport width and time span; rollup tier is chosen by span (<=2h raw, <=24h 1-minute, else 1-hour).
- All JS interop and localStorage access guard against `InvalidOperationException`/`JSDisconnectedException` (prerender / circuit-disconnected) and silently degrade.
- `Dispose` cancels the auto-refresh `CancellationTokenSource` and detaches all source `Changed` / theme events.
- The server-name map (`BuildServerOptions`) labels each server by its configured `DedicatedServerDefinition.DisplayName` (falling back to the unique name only when blank), so the filter checkboxes and the chart series legends show the operator-chosen name rather than the unique name / id.
