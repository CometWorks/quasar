# Quasar/Services/Analytics/ServerMetricsStore.cs

**Module:** Quasar.Services.Analytics  **Kind:** class  **Tier:** 2

## Summary

Holds the three-tier RRD-style metric history for one dedicated server: a raw per-second circular buffer (1 hour) plus one-minute and one-hour rollup buffers whose capacities are derived from the analytics retention policy (`AnalyticsStoreOptions.RetentionDays`).

## Structure

Namespace: `Quasar.Services.Analytics`

**`ServerMetricsStore`** (sealed class)

Constants:
- `DefaultRawCapacity = 3600`
- `RollupMinuteCapacityPerDay = 24 * 60`
- `RollupHourCapacityPerDay = 24`

Constructor: `ServerMetricsStore(AnalyticsStoreOptions? options = null)` — reads `RetentionDays` (falling back to `AnalyticsStoreOptions.DefaultRetentionDays`) to size the rollup buffers; capacities are floored at 1.

Properties:
- `Raw : RrdCircularBuffer` — 3600-slot circular buffer of raw `MetricSample` values (one per second)
- `OneMinute : RrdRollupBuffer` — `RetentionDays * 24 * 60` slots, 60-second aggregation window
- `OneHour : RrdRollupBuffer` — `RetentionDays * 24` slots, 3600-second aggregation window

Methods:
- `Ingest(in MetricSample)` — pushes into `Raw`; only when `Raw.Push` returns `true` (slot advanced) does it forward to `OneMinute.Observe` and `OneHour.Observe`
- `Restore(IReadOnlyList<MetricSample> raw, oneMinute, oneHour)` — bulk-replaces all three buffers from deserialized disk data; used by `MetricsStoreService.TryLoadFromDiskAsync`

## Dependencies

- [`Quasar/Services/Analytics/AnalyticsStoreOptions.cs`](AnalyticsStoreOptions.cs.md)
- [`Quasar/Services/Analytics/MetricSample.cs`](MetricSample.cs.md)
- [`Quasar/Services/Analytics/RrdCircularBuffer.cs`](RrdCircularBuffer.cs.md)
- [`Quasar/Services/Analytics/RrdRollupBuffer.cs`](RrdRollupBuffer.cs.md)

## Notes

- `Ingest` only advances rollups when the raw push reports a new slot, keeping rollups in lockstep with the raw slot clock instead of reacting to every sample.
- Instantiated per server by `MetricsStoreService`; all concurrent access is serialised through that service's single-reader channel ingest loop, so the store itself is not internally locked.
