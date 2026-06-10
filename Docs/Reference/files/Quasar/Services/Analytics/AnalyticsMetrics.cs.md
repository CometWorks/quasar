# Quasar/Services/Analytics/AnalyticsMetrics.cs

**Module:** Quasar.Services.Analytics  **Kind:** record + class  **Tier:** 2

## Summary

Central catalogue of analytics chart panels exposed by the `/analytics` dashboard and `/api/analytics/series` endpoint. Scalar entries define metric key, panel title/subtitle, sample selector, availability check, axis formatting, and fixed/dynamic Y-axis behaviour. Profiler entries define continuous game-loop timing selectors and deep-profiler top-entry selectors while keeping browser chart rendering on the same existing payload shape.

## Structure

Namespace: `Quasar.Services.Analytics`

**`AnalyticsMetric`** (`sealed record`)

Carries metric display and extraction metadata:
- `Key`, `Title`, `Subtitle`
- `Selector : Func<MetricSample,double>`
- `IsAvailable : Func<MetricSample,bool>`
- `RequiresZero`, `Decimals`, `Kilo`, `FixedMax`, `DynamicMaxStep5`

**`AnalyticsMetrics`** (`public static class`)

Members:
- `All : IReadOnlyList<AnalyticsMetric>` — default panel order and supported scalar metric set.
- `Panels : IReadOnlyList<AnalyticsPanelDefinition>` — combined scalar/profiler panel metadata used by the Analytics page.
- `Find(string? key) : AnalyticsMetric?` — case-insensitive lookup.
- `FindPanel(string? key) : AnalyticsPanelDefinition?` — case-insensitive panel metadata lookup.

Default metrics:
- `simspeed`, `cpu`, `memory`, `players`, `frametime`, `pcu`
- `grids`, `entities`

**`ProfilerAnalyticsMetrics`** (`public static class`)

Members:
- `All : IReadOnlyList<ProfilerAnalyticsMetric>` — profiler timing buckets appended after scalar panels.
- `Find(string? key) : ProfilerAnalyticsMetric?` — case-insensitive lookup.

Profiler metrics:
- `profiler-frame`, `profiler-update`, `profiler-physics`
- `profiler-scripts`, `profiler-network`, `profiler-other`

**`ProfilerEntryAnalyticsMetrics`** (`public static class`)

Members:
- `All : IReadOnlyList<ProfilerEntryAnalyticsMetric>` — deep profiler top-list panels appended after game-loop profiler panels.
- `Find(string? key) : ProfilerEntryAnalyticsMetric?` — case-insensitive lookup.

Profiler entry metrics:
- `profiler-top-grids`
- `profiler-top-entities`

## Dependencies

- [`Quasar/Services/Analytics/MetricSample.cs`](MetricSample.cs.md)
- [`Quasar/Services/Analytics/AnalyticsSeriesService.cs`](AnalyticsSeriesService.cs.md)
- [`Magnetar.Protocol/Model/ProfilerSnapshot.cs`](../../../Magnetar.Protocol/Model/ProfilerSnapshot.cs.md)

## Notes

Profiler panel metadata is separate from scalar sample selectors so the chart page can add profiler panels without changing the browser chart interop. Entry-backed profiler panels use Quasar's own `ProfilerSnapshot` top lists.
