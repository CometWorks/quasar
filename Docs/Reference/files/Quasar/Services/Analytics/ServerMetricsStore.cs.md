# Quasar/Services/Analytics/ServerMetricsStore.cs

**Module:** Quasar.Services.Analytics  **Kind:** class  **Tier:** 2

## Summary

Holds the three-tier RRD-style metric history for one dedicated server: a raw per-second circular buffer (1 hour), and rollup buffers sized from analytics retention policy (`RetentionDays`) for one-minute and one-hour snapshots.

## Structure

Namespace: `Quasar.Services.Analytics`

**`ServerMetricsStore`** (sealed class)

Properties:
- `Raw : RrdCircularBuffer` — 3600-slot circular buffer of raw `MetricSample` values (one per second)
- `OneMinute : RrdRollupBuffer` — `RetentionDays * 24 * 60` slots, with 60-second aggregation window
- `OneHour : RrdRollupBuffer` — `RetentionDays * 24` slots, with 3600-second aggregation window

Methods:
- `Ingest(in MetricSample)` — pushes a sample into `Raw`; if `Raw.Push` returns `true` (slot advanced), forwards to `OneMinute.Observe` and `OneHour.Observe`
- `Restore(IReadOnlyList<MetricSample> raw, oneMinute, oneHour)` — bulk-replaces all three buffers from deserialized disk data; used by `MetricsStoreService.TryLoadFromDiskAsync`

## Dependencies

- [`Quasar/Services/Analytics/MetricSample.cs`](MetricSample.cs.md)
- [`Quasar/Services/Analytics/RrdCircularBuffer.cs`](RrdCircularBuffer.cs.md)
- [`Quasar/Services/Analytics/RrdRollupBuffer.cs`](RrdRollupBuffer.cs.md)

## Notes

- `Ingest` only forwards to rollup buffers when `Raw.Push` returns `true`, ensuring rollups advance in lockstep with the raw slot clock rather than on every sample.
- The store is instantiated per server by `MetricsStoreService`; all concurrent access is serialised through the service's single-reader channel ingest loop.
