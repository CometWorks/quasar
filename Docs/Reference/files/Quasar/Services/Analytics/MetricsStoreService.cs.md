# Quasar/Services/Analytics/MetricsStoreService.cs

**Module:** Quasar.Services.Analytics  **Kind:** class  **Tier:** 2

## Summary

`IHostedService` that owns the per-server metric stores, drives the single-reader ingest loop via a bounded `Channel`, and periodically persists all stores to disk as compact JSON. On startup it loads previously saved data from disk; on shutdown it drains the channel and performs a final persist.

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
- `PersistAllAsync(CancellationToken) : Task` — serialises all stores to disk atomically; guarded by `_persistInFlight` flag to avoid concurrent persists

Private internals:
- `IngestLoopAsync` — single-reader loop; after every 100 items checks if 7 minutes have elapsed since last persist and fires `PersistAllAsync` fire-and-forget
- `PersistStoreAsync` — serialises one store into `PersistedAnalyticsDocument` JSON; writes via `AtomicFileWriter`
- `TryLoadFromDiskAsync` — reads and deserialises a store file; calls `store.Restore`
- `PersistedAnalyticsDocument` / `PersistedMetricSample` (private nested classes) — compact JSON envelope using short property names (`"r"`, `"m"`, `"h"`, `"T"`, `"Ss"`, etc.)

## Dependencies

- [`Quasar/Services/Analytics/ServerMetricsStore.cs`](ServerMetricsStore.cs.md)
- [`Quasar/Services/Analytics/MetricSample.cs`](MetricSample.cs.md)
- [`Quasar/Services/AtomicFileWriter.cs`](../AtomicFileWriter.cs.md) (via `AtomicFileWriter.WriteTextAsync`)
- [`Quasar/Services/DedicatedServerCatalog.cs`](../DedicatedServerCatalog.cs.md) (to enumerate servers at startup)
- `Magnetar.Protocol.Runtime.MagnetarPaths` (for analytics file path resolution)
- BCL: `System.Threading.Channels`, `System.Text.Json`, `System.Collections.Concurrent`

## Notes

- The channel is bounded at 512 with `DropOldest` so the ingest loop never blocks callers, but very fast producers under sustained overload will lose the oldest samples before they are stored.
- `PersistAllAsync` uses an `Interlocked.Exchange` flag to prevent concurrent persist runs; a background fire-and-forget call from the ingest loop ignores the return value, meaning persist errors are only logged with `LogWarning`.
- JSON property names are deliberately shortened (single/two-letter keys) to reduce file size.
