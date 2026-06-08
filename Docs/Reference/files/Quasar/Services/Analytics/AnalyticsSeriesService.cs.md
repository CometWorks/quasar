# Quasar/Services/Analytics/AnalyticsSeriesService.cs

**Module:** Quasar.Services.Analytics  **Kind:** class + records  **Tier:** 2

## Summary

Builds compact chart payloads for the analytics HTTP endpoint. It reads scalar metric samples from `MetricsStoreService` and profiler timing windows from `ProfilerStoreService`, buckets each selected server onto a shared timeline, and returns aligned series arrays for browser-side uPlot rendering.

## Structure

Namespace: `Quasar.Services.Analytics`

**`AnalyticsSeriesService`** (`sealed class`)

Constants:
- `MaxPointsCeiling = 1000`, `MaxPointsFloor = 10`

Constructor:
- `AnalyticsSeriesService(MetricsStoreService store, ProfilerStoreService profilerStore)`

Methods:
- `Build(long fromUnix, long toUnix, IReadOnlyList<string> servers, IReadOnlyList<string> metricKeys, int maxPoints) : AnalyticsSeriesResponse` — validates range/inputs, resolves scalar metrics via `AnalyticsMetrics.Find` and profiler metrics via `ProfilerAnalyticsMetrics.Find`, buckets values, drops empty scalar buckets, and returns profiler chart DTOs even when their current series are empty.
- `BuildMetricCharts(...)` — preserves the scalar metric path backed by raw/rollup samples from `MetricsStoreService`.
- `BuildProfilerCharts(...)` — reads normalized profiler snapshots and emits the same chart DTO shape as scalar metrics, including empty per-server profiler series for selected servers with no points in the requested range.

Private helpers:
- `ResolveMax(AnalyticsMetric, double)` — applies fixed or dynamic Y-axis max.
- `ResolveBuckets(long, long, int)` — computes the shared bucket width/count for scalar and profiler charts.
- `ReadSamplesForRange(ServerMetricsStore, long, long, long)` — raw samples for <=2h, 1-minute rollups for <=24h, 1-hour rollups beyond.
- `ServerBuckets` — per-server bucket sums/counts for every requested metric.

DTO records:
- `AnalyticsSeriesResponse`
- `AnalyticsChartDto`
- `AnalyticsAxisDto`
- `AnalyticsSeriesDto`

## Dependencies

- [`Quasar/Services/Analytics/MetricsStoreService.cs`](MetricsStoreService.cs.md)
- [`Quasar/Services/Analytics/ProfilerStoreService.cs`](ProfilerStoreService.cs.md)
- [`Quasar/Services/Analytics/ServerMetricsStore.cs`](ServerMetricsStore.cs.md)
- [`Quasar/Services/Analytics/MetricSample.cs`](MetricSample.cs.md)
- [`Quasar/Services/Analytics/AnalyticsMetrics.cs`](AnalyticsMetrics.cs.md)

## Notes

Scalar metrics keep their `IsAvailable` predicates so missing values can be skipped instead of plotted as false zeroes. Profiler charts reuse the existing `/api/analytics/series` payload and browser chart renderer; only the selected metric definitions determine whether a chart is scalar-backed or profiler-backed. Requested profiler panels are still returned with empty arrays when no profiler samples match, keeping their chart windows visible.
