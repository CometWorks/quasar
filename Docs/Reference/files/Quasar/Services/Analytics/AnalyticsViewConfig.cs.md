# Quasar/Services/Analytics/AnalyticsViewConfig.cs

**Module:** Quasar.Services.Analytics  **Kind:** class  **Tier:** 2

## Summary

Defines three plain-data types that capture the user's analytics dashboard preferences: which time range to display, grid layout dimensions, selected metric names, and per-panel visibility and ordering. These objects are serialised into user/session state by the analytics Blazor page and are not persisted to disk by the analytics service itself.

## Structure

Namespace: `Quasar.Services.Analytics`

**`AnalyticsViewConfig`** (sealed class) — top-level view preferences
- `SelectedRangeKey` — display-range identifier (default `"1h"`)
- `AutoRefreshSeconds` — polling interval (0 = disabled)
- `GridColumns` / `GridRows` / `RowHeightPx` — dashboard grid dimensions
- `MaxVisibleServersForCharts` — hard cap on simultaneous rendered server lines per chart (default 6)
- `ShowAllServers` — bypass `MaxVisibleServersForCharts` and render all selected lines
- `SelectedUniqueNames` — instance filter list
- `CustomFromDate` / `CustomFromTime` / `CustomToDate` / `CustomToTime` — custom date-range bounds
- `Panels` — ordered list of `AnalyticsPanelConfig`

**`AnalyticsPanelConfig`** (sealed class) — per-panel layout record
- `Key`, `Visible`, `Order`, `ColumnSpan`, `RowSpan`

**`AnalyticsPanelDialogResult`** (sealed class) — dialog return value for panel editing
- `Visible`, `Order`, `ColumnSpan`, `RowSpan`

## Dependencies

None (no external types referenced).
