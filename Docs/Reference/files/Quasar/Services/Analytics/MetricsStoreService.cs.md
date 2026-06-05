# Quasar/Services/Analytics/MetricsStoreService.cs

**Module:** Quasar.Services.Analytics  **Kind:** class  **Tier:** 2

## Summary

`IHostedService` that owns the per-server metric stores, drives the single-reader ingest loop via a bounded `Channel`, and persists samples to disk in an append-only JSONL format. On startup it loads from `analytics.jsonl`, then lazily appends only new points.

## Structure

Namespace: `Quasar.Services.Analytics`

**`MetricsStoreService`** (sealed class) — implements `IHostedService`, `IDisposable`

Public API:
- `StartAsync(CancellationToken)` — loads persisted data for all known servers, then starts the background ingest loop task
- `StopAsync(CancellationToken)` — completes the channel writer, awaits the loop, then calls `PersistAllAsync`
- `Dispose()` — cancels the shutdown token (idempotent via `Interlocked.Exchange`)
- `Enqueue(string uniqueName, in MetricSample)` — fire-and-forget write into the bounded channel (drops oldest when full)
- `GetStore(string uniqueName) : ServerMetricsStore?` — returns the in-memory store for a server
- `GetUniqueNames() : IReadOnlyList<string>` — sorted list of all server names with stores
- `PersistAllAsync(CancellationToken) : Task` — appends new points to disk, guarded by `_persistInFlight` flag to avoid concurrent persists
- `PersistAllAsync(CancellationToken) : Task` — appends only new sample points since last successful flush; periodically rewrites JSONL file for retention-bound compaction

Private internals:
- `IngestLoopAsync` — single-reader loop; after every 100 items checks if 7 minutes have elapsed since last persist and fires `PersistAllAsync` fire-and-forget
- `PersistStoreAsync` — reads in-memory buffers, writes only new points (`PersistedMetricLogLine`) since last persist watermark, and periodically rewrites into bounded-retention JSONL
- `TryLoadFromDiskAsync` — reads `analytics.jsonl` by lines, then calls `store.Restore` with retention-filtered data

## Dependencies

- [`Quasar/Services/Analytics/ServerMetricsStore.cs`](ServerMetricsStore.cs.md)
- [`Quasar/Services/Analytics/MetricSample.cs`](MetricSample.cs.md)
- [`Quasar/Services/DedicatedServerCatalog.cs`](../DedicatedServerCatalog.cs.md) (to enumerate servers at startup)
- `Magnetar.Protocol.Runtime.MagnetarPaths` (for analytics file path resolution)
- BCL: `System.Threading.Channels`, `System.Text.Json`, `System.Collections.Concurrent`

## Notes

- The channel is bounded at 512 with `DropOldest` so the ingest loop never blocks callers, but very fast producers under sustained overload will lose the oldest samples before they are stored.
- `PersistAllAsync` uses an `Interlocked.Exchange` flag to prevent concurrent persist runs; a background fire-and-forget call from the ingest loop ignores the return value, meaning persist errors are only logged with `LogWarning`.
- JSONL lines use short fields (`"b"`/`"r"`, `"m"`, `"h"` and compact metric keys), then optional compaction rewrites into retention-limited JSONL.

## Internal types

- `PersistedMetricLogLine` — per-line JSONL record including bucket discriminator (`r` raw, `m` one-minute, `h` one-hour)
- `PersistProgress` — per-server watermark of last persisted timestamp per bucket for incremental writes
- `LoadedAnalyticsData` — in-memory loader container with per-bucket samples
