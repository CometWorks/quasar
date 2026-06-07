# Quasar/Services/Analytics/AnalyticsMetrics.cs

**Module:** Quasar.Services.Analytics  **Kind:** record + class  **Tier:** 2

## Summary

Central catalogue of analytics chart panels exposed by the `/analytics` dashboard and `/api/analytics/series` endpoint. Scalar entries define metric key, panel title/subtitle, sample selector, availability check, axis formatting, and fixed/dynamic Y-axis behaviour. Profiler entries define sampled game-loop timing selectors so regular metrics and profiler buckets share one panel catalogue.

## Structure

Namespace: `Quasar.Services.Analytics`

**`AnalyticsMetric`** (`sealed record`)

Carries metric display and extraction metadata:
- `Key`, `Title`, `Subtitle`
- `Selector : Func<MetricSample,double>`
- `IsAvailable : Func<MetricSample,bool>`
- `RequiresZero`, `Decimals`, `Kilo`, `FixedMax`, `DynamicMaxStep5`

**`AnalyticsPanelDefinition`** (`sealed record`)

Carries the common panel metadata used by the Blazor page:
- `Key`, `Title`, `Subtitle`

**`ProfilerAnalyticsMetric`** (`sealed record`)

Carries profiler display and extraction metadata:
- `Key`, `Title`, `Subtitle`
- `Selector : Func<ProfilerTimingBreakdown,double>`
- `RequiresZero`, `Decimals`, `Kilo`, `FixedMax`

**`AnalyticsMetrics`** (`public static class`)

Members:
- `All : IReadOnlyList<AnalyticsMetric>` — default panel order and full supported metric set.
- `Panels : IReadOnlyList<AnalyticsPanelDefinition>` — combined scalar/profiler panel order used by the Analytics page.
- `Find(string? key) : AnalyticsMetric?` — case-insensitive lookup.
- `FindPanel(string? key) : AnalyticsPanelDefinition?` — case-insensitive panel metadata lookup.

Default metrics:
- `simspeed`, `cpu`, `memory`, `players`, `frametime`, `pcu`
- `grids`, `entities`, `blocks`, `floating`

**`ProfilerAnalyticsMetrics`** (`public static class`)

Members:
- `All : IReadOnlyList<ProfilerAnalyticsMetric>` — profiler chart buckets appended after scalar analytics panels.
- `Find(string? key) : ProfilerAnalyticsMetric?` — case-insensitive lookup.

Profiler metrics:
- `profiler-frame`, `profiler-update`, `profiler-physics`
- `profiler-scripts`, `profiler-network`, `profiler-other`

## Dependencies

- [`Quasar/Services/Analytics/MetricSample.cs`](MetricSample.cs.md)
- [`Quasar/Services/Analytics/AnalyticsSeriesService.cs`](AnalyticsSeriesService.cs.md)
- [`Magnetar.Protocol/Model/ProfilerSnapshot.cs`](../../../Magnetar.Protocol/Model/ProfilerSnapshot.cs.md)

## Notes

`blocks` and `floating` are optional sample fields; their availability checks hide missing historical data instead of plotting zeroes for analytics files written before those fields existed.
