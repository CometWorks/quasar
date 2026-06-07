# Quasar/Services/Analytics/ProfilerStoreService.cs

**Module:** Quasar.Services.Analytics  **Kind:** class + records  **Tier:** 2

## Summary

Bounded in-memory store for recent profiler snapshots per server. `AgentRegistry` enqueues validated profiler windows as agent snapshots arrive; `AnalyticsSeriesService`, the analytics page, and `/api/analytics/profiler` query this store for selected servers and time ranges.

## Structure

Namespace: `Quasar.Services.Analytics`

**`ProfilerStoreService`** (`public sealed class`)

Members:
- `Enqueue(string uniqueName, ProfilerSnapshot snapshot)` — validates/normalizes then appends new completed windows to the per-server queue, dropping repeated copies of the same already-published window.
- `Build(long fromUnix, long toUnix, IReadOnlyList<string> servers) : ProfilerSeriesResponse` — returns matching snapshots grouped by server.
- `ClampTopCount(int count)` — internal helper shared by the validator.

Records:
- `ProfilerSeriesResponse`
- `ProfilerServerSeries`

## Dependencies

- [`Quasar/Services/Analytics/ProfilerSnapshotValidator.cs`](ProfilerSnapshotValidator.cs.md)
- [`Magnetar.Protocol/Model/ProfilerSnapshot.cs`](../../../Magnetar.Protocol/Model/ProfilerSnapshot.cs.md)

## Notes

Retention is deliberately small and in-memory (720 samples per server, roughly 12 hours at the default one-minute cadence). The persisted RRD path remains reserved for regular scalar analytics.
