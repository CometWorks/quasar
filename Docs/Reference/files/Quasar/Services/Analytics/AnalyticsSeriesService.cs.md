# Quasar/Services/Analytics/AnalyticsSeriesService.cs

**Module:** Quasar.Services.Analytics  **Kind:** class + records  **Tier:** 2

## Summary

Builds compact chart payloads for the analytics HTTP endpoint. It reads scalar metric samples from `MetricsStoreService` and profiler timing windows from `ProfilerStoreService`, buckets each server onto shared timelines, and returns aligned series arrays for browser-side rendering in the same chart grid.

## Structure

Namespace: `Quasar.Services.Analytics`

**`AnalyticsSeriesService`** (`sealed class`)

Constants:
- `MaxPointsCeiling = 1000`, `MaxPointsFloor = 10`

Constructor:
- `AnalyticsSeriesService(MetricsStoreService store, ProfilerStoreService profilerStore)`

Methods:
- `Build(long fromUnix, long toUnix, IReadOnlyList<string> servers, IReadOnlyList<string> metricKeys, int maxPoints) : AnalyticsSeriesResponse` — validates range/inputs, resolves scalar metrics via `AnalyticsMetrics.Find` and profiler metrics via `ProfilerAnalyticsMetrics.Find`, reads samples/windows, buckets values, drops empty buckets, and returns one chart DTO per metric with available data.
- `BuildMetricCharts(...)` — builds regular persisted analytics charts from RRD samples.
- `BuildProfilerCharts(...)` — builds profiler timing charts from recent in-memory profiler windows.

Private helpers:
- `ResolveMax(AnalyticsMetric, double)` — applies fixed or dynamic Y-axis max.
- `ResolveBuckets(long, long, int)` — computes shared bucket width/count for scalar and profiler charts.
- `ReadSamplesForRange(ServerMetricsStore, long, long, long)` — raw samples for <=2h, 1-minute rollups for <=24h, 1-hour rollups beyond.
- `ServerBuckets` — per-server bucket sums/counts for every requested metric.

DTO records:
- `AnalyticsSeriesResponse`
- `AnalyticsChartDto`
- `AnalyticsAxisDto`
- `AnalyticsSeriesDto`

## Dependencies

- [`Quasar/Services/Analytics/MetricsStoreService.cs`](MetricsStoreService.cs.md)
- [`Quasar/Services/Analytics/ServerMetricsStore.cs`](ServerMetricsStore.cs.md)
- [`Quasar/Services/Analytics/MetricSample.cs`](MetricSample.cs.md)
- [`Quasar/Services/Analytics/AnalyticsMetrics.cs`](AnalyticsMetrics.cs.md)
- [`Quasar/Services/Analytics/ProfilerStoreService.cs`](ProfilerStoreService.cs.md)

## Notes

Each scalar metric uses its own `IsAvailable` predicate, so optional telemetry fields such as block count and floating-object count do not create false zeroes when absent. Profiler charts are sparse point series from completed sample windows, not persisted RRD data.
