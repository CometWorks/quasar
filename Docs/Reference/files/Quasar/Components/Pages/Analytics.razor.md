# Quasar/Components/Pages/Analytics.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Interactive `/analytics` dashboard that renders rolling Space Engineers server metrics, profiler timing buckets, and deep profiler top grid/entity charts as uPlot time-series charts. It reads scalar samples from `MetricsStoreService`, fetches chart series over `/api/analytics/series`, supports per-server selection, a standalone Analytics Profiles panel for live/startup profiler mode control, a configurable CSS-grid panel layout, persisted view config (localStorage), auto-refresh, custom time ranges, theme-aware chart styling, and a per-panel settings dialog.

## Structure
- `@page "/analytics"`, `@implements IDisposable`.
- **`[Inject]`ed services:** `MetricsStoreService MetricsStore`, `DedicatedServerCatalog ServerCatalog`, `AgentRegistry Registry`, `ProfilerStoreService ProfilerStore`, `ISnackbar Snackbar`, `ILocalStorageService LocalStorage`, `IDialogService DialogService`, `ThemePreferenceService ThemePreference`, `IJSRuntime JS`. No `[Parameter]`s.
- **Toolbar:** time-range `MudSelect` (30s..30d + Custom), auto-refresh `MudSelect` (Off/5/15/30/60s), Refresh / Export / Reset-layout buttons, and an "Add panel" `MudMenu` listing hidden panels. Custom range shows date/time pickers.
- **Filters paper:** server checkbox grid with "Select all" / "Select none"; grid controls (`Columns`, `Rows`, `Row height`) plus "Reset panels".
- **Analytics Profiles panel:** standalone `MudExpansionPanel` between the server/grid filters and the charts. It is collapsed by default, opens automatically while any selected server is configured for or running `DeepContinuous`, and renders only while expanded (`KeepContentAlive=false`). The panel shows only selected servers, combines each server's startup profiler mode selector with live-mode status, explains whether changes are saved for future starts and/or applied to the running agent immediately, and includes buttons to reveal the simple profiler chart panels or the extensive Top Grids / Entity Types panels.
- **Summary chips row:** Servers, SimSpeed, CPU, Memory, Players, PCU, Grids, Entities, Range Avg Sim.
- **Chart grid:** CSS-grid (`analytics-chart-grid`) of `MudPaper` cards, each containing a stable `div` target rendered by `quasar-charts.js`; panel settings use a "Tune" `MudIconButton` opening `AnalyticsPanelDialog`.
- **Metrics:** panel metadata comes from `AnalyticsMetrics.Panels`: scalar metrics (`simspeed`, `cpu`, `memory`, `players`, `frametime`, `pcu`, `grids`, `entities`), profiler timing buckets (`profiler-frame`, `profiler-update`, `profiler-physics`, `profiler-scripts`, `profiler-network`, `profiler-other`), and deep profiler top-entry panels (`profiler-top-grids`, `profiler-top-entities`).
- **Refresh pipeline:** `RefreshView` resolves range/servers, reads in-memory scalar samples for summary chips, checks profiler samples when scalar samples are absent, syncs default Analytics Profiles expansion, and builds visible chart descriptors. Visible simple and extensive profiler panels stay mounted for selected servers even when the current window has no profiler points, so the browser can render empty profiler charts instead of collapsing the panel. `SyncChartsAsync` sends a compact descriptor to JS; the browser fetches scalar/profiler series data directly. Source-change events are coalesced through `ProcessQueuedRefreshAsync`; auto-refresh runs a `PeriodicTimer` loop.
- **Persistence/config:** `AnalyticsViewConfig` (storage key `quasar.analytics.view.v2`) with `NormalizeConfig`, `CreateDefaultPanels`, panel visibility/order/spans.
- **Profiler mode changes:** `HandleProfilerModeChanged` normalises the selected protocol value, persists it to `DedicatedServerCatalog` when the server definition exists, sends `ServerCommandType.SetProfilerMode` through `AgentRegistry.SendCommandAndWaitAsync` for connected agents, and reports explicitly whether the change was saved for future starts, applied to the running agent now, or both.
- **Helper records/classes:** `ServerOption`, `ProfilerModeOption`, `ProfilerView`, `SummaryChipModel`, `ChartCard`, `ChartSyncResult`.

## Dependencies
- `Quasar/Components/Pages/AnalyticsPanelDialog.razor` (panel settings dialog)
- [`Quasar/Services/Analytics/MetricsStoreService.cs`](../../Services/Analytics/MetricsStoreService.cs.md)
- [`Quasar/Services/Analytics/AnalyticsSeriesService.cs`](../../Services/Analytics/AnalyticsSeriesService.cs.md)
- [`Quasar/Services/Analytics/ProfilerStoreService.cs`](../../Services/Analytics/ProfilerStoreService.cs.md)
- `Quasar/Services/DedicatedServerCatalog.cs`
- `Quasar/Services/AgentRegistry.cs`
- [`Quasar/Services/ThemePreferenceService.cs`](../../Services/ThemePreferenceService.cs.md)
- [`Quasar/Components/Pages/Analytics.razor.css`](Analytics.razor.css.md) (scoped styles)
- External: **MudBlazor**, **Blazored.LocalStorage** (`ILocalStorageService`), `Microsoft.JSInterop`, uPlot via `quasar-charts.js`.

## Notes
- Bulk chart points are fetched by the browser instead of pushed through the Blazor SignalR circuit; the server chooses scalar rollup tier by span (<=2h raw, <=24h 1-minute, else 1-hour).
- All JS interop and localStorage access guard against `InvalidOperationException`/`JSDisconnectedException` (prerender / circuit-disconnected) and silently degrade.
- `Dispose` cancels the auto-refresh `CancellationTokenSource` and detaches all source `Changed` / theme events.
- The server-name map (`BuildServerOptions`) labels each server by its configured `DedicatedServerDefinition.DisplayName` (falling back to the unique name only when blank), so the filter checkboxes and the chart series legends show the operator-chosen name rather than the unique name / id.
