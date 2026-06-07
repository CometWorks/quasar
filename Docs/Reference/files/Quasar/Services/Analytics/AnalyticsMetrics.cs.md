# Quasar/Services/Analytics/AnalyticsMetrics.cs

**Module:** Quasar.Services.Analytics  **Kind:** record + class  **Tier:** 2

## Summary

Central catalogue of scalar analytics chart metrics exposed by the `/analytics` dashboard and `/api/analytics/series` endpoint. Entries define metric key, panel title/subtitle, sample selector, availability check, axis formatting, and fixed/dynamic Y-axis behaviour.

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
- `Find(string? key) : AnalyticsMetric?` — case-insensitive lookup.

Default metrics:
- `simspeed`, `cpu`, `memory`, `players`, `frametime`, `pcu`
- `grids`, `entities`

## Dependencies

- [`Quasar/Services/Analytics/MetricSample.cs`](MetricSample.cs.md)
- [`Quasar/Services/Analytics/AnalyticsSeriesService.cs`](AnalyticsSeriesService.cs.md)

## Notes

This file intentionally describes only the scalar metrics consumed by the reverted analytics chart page.
