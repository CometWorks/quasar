# Quasar/Services/Analytics/AnalyticsViewConfig.cs

**Module:** Quasar.Services.Analytics  **Kind:** class  **Tier:** 2

## Summary

Plain settings/state objects backing the ApexCharts-based analytics dashboard page. `AnalyticsViewConfig` captures the user's chosen time range, auto-refresh cadence, server selection and the per-panel grid layout; `AnalyticsPanelConfig` describes one chart panel's placement; `AnalyticsPanelDialogResult` carries the edited layout values back from the per-panel configuration dialog. These are serialised into user/session state by the analytics Blazor page, not by the analytics service.

## Structure

Namespace: `Quasar.Services.Analytics`

**`AnalyticsViewConfig`** (sealed class) — overall dashboard view state:
- `SelectedRangeKey : string` — time-range preset key, default `"1h"`
- `AutoRefreshSeconds : int` — auto-refresh interval (0 = off)
- `GridColumns : int` (default 2), `GridRows : int` (default 4), `RowHeightPx : int` (default 320) — chart grid geometry
- `MaxVisibleServersForCharts : int` (default 6) — cap on per-series servers drawn
- `ShowAllServers : bool` (default false) — bypass the cap and render all selected lines
- `SelectedUniqueNames : List<string>` — explicitly selected server unique names
- `CustomFromDate`/`CustomFromTime`/`CustomToDate`/`CustomToTime : DateTime?/TimeSpan?` — custom range bounds (defaults: yesterday 00:00 → today 23:59 UTC)
- `Panels : List<AnalyticsPanelConfig>` — per-panel layout entries

**`AnalyticsPanelConfig`** (sealed class) — one panel: `Key` (panel id), `Visible` (default true), `Order`, `ColumnSpan` (default 1), `RowSpan` (default 1).

**`AnalyticsPanelDialogResult`** (sealed class) — return shape from the panel-edit dialog: `Visible`, `Order`, `ColumnSpan`, `RowSpan` (same defaults as `AnalyticsPanelConfig` minus the key).

## Dependencies

None (uses only BCL types).

## Notes

- Pure DTO/state holders with no behavior. Default date/time bounds are evaluated at construction time from `DateTime.UtcNow`.
