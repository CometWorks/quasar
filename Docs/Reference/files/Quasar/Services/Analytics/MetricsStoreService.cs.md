# Quasar/Services/Analytics/MetricsStoreService.cs

**Module:** Quasar.Services.Analytics  **Kind:** class  **Tier:** 2

## Summary

Hosted background service that owns one `ServerMetricsStore` per dedicated server, ingests metric samples off a bounded channel, and persists/loads each server's history to a per-server JSONL analytics file. Handles retention pruning, incremental append, periodic/size-triggered compaction (full rewrite), and crash-safe atomic file replacement. The single hub that analytics charts and the metrics writer pipeline both talk to.

## Structure

Namespace: `Quasar.Services.Analytics`

**`MetricsStoreService`** (sealed class) implements `IHostedService`, `IDisposable`.

Constants/config:
- `CompactEveryMinutes = 720` (12h), `CompactionMaxFileSizeBytes = 32 MiB` — compaction triggers
- `ValidBuckets = ["r","m","h"]` with `RawBucket`/`OneMinuteBucket`/`OneHourBucket` constants
- `JsonOptions` — Web defaults, ignore-null, compact

State/fields:
- `_catalog : DedicatedServerCatalog`, `_analyticsStoreOptions : AnalyticsStoreOptions`, `_logger`
- `_stores : ConcurrentDictionary<string, ServerMetricsStore>` (case-insensitive)
- `_persistProgress : ConcurrentDictionary<string, PersistProgress>` — last-persisted timestamp per bucket per server
- `_channel : Channel<(string, MetricSample)>` — bounded(512), `DropOldest`, single-reader/multi-writer
- `_shutdown`, `_ingestLoopTask`, `_lastPersistUtc`, `_nextCompactionUtc`, `_itemsSincePersistCheck`, `_persistInFlight`, `_disposed`

Public members:
- `event Action? Changed` — raised after each ingested sample
- `StartAsync` — creates/loads stores from disk for catalog servers, sets compaction clock, then launches the ingest loop
- `StopAsync` — completes the channel, awaits the loop, then `PersistAllAsync`
- `Dispose()` — idempotent (`Interlocked.Exchange`); cancels and disposes the shutdown token
- `Enqueue(string, in MetricSample)` — ensures a store exists and writes the sample to the channel (non-blocking, drops oldest under back-pressure)
- `GetStore(string) : ServerMetricsStore?`, `GetUniqueNames() : IReadOnlyList<string>`
- `PersistAllAsync(CancellationToken)` — guarded by `_persistInFlight`; persists every store in name order

Private logic:
- `IngestLoopAsync` — drains channel, `store.Ingest`, raises `Changed`; every 100 items, if >7 min since last persist, fires `PersistAllAsync` fire-and-forget
- `GetOrCreateStore` — creates `ServerMetricsStore(_analyticsStoreOptions)` + progress entry
- `PersistStoreAsync` — appends new lines since each bucket's last-persisted timestamp, then compacts (rewrite) if due or file overgrown
- `AppendSeriesLinesAsync` — async append-mode `FileStream`, one JSON line per sample, explicit flush
- `RewriteStoreAsJsonlAsync` — full rewrite to a temp file then `File.Move(overwrite: true)`; deletes the file if no in-retention data remains
- `TryLoadFromDiskAsync` / `TryLoadJsonlFromDiskAsync` — parse JSONL, drop out-of-retention lines, `store.Restore`, compact if stale data found
- `BuildSeriesLines`, `GetRetentionCutoffUnixSeconds`, `IsAnalyticsFileOvergrown`, `IsValidBucket`, `BuildTempPath`, `TryDelete`

Nested types:
- `PersistProgress` — per-bucket last-persisted unix timestamps
- `LoadedAnalyticsData` — parsed raw/minute/hour lists + `HasOutOfRetentionData`
- `PersistedMetricLogLine` — on-disk JSON shape (short property names `b`,`T`,`Ss`,`Cpu`,`Mem`,`Ft`,`P`,`Pcu`,`G`,`E`) with `From`/`ToMetricSample` converters

## Dependencies

- [`Quasar/Services/Analytics/ServerMetricsStore.cs`](ServerMetricsStore.cs.md)
- [`Quasar/Services/Analytics/MetricSample.cs`](MetricSample.cs.md)
- [`Quasar/Services/Analytics/AnalyticsStoreOptions.cs`](AnalyticsStoreOptions.cs.md)
- [`Quasar/Services/DedicatedServerCatalog.cs`](../DedicatedServerCatalog.cs.md) — enumerates servers at startup
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs` — `GetQuasarServerAnalyticsPath(uniqueName)`
- `Quasar/Models` (namespace import)
- External: `System.Threading.Channels`, `System.Text.Json`, `System.Collections.Concurrent`, `Microsoft.Extensions.Hosting` (`IHostedService`), `Microsoft.Extensions.Logging`

## Notes

- Concurrency: writers call `Enqueue` from anywhere; a single reader (`IngestLoopAsync`) mutates stores, so `ServerMetricsStore` needs no internal locking. The channel drops the oldest sample under sustained back-pressure rather than blocking the producer.
- Persistence is append-first (cheap, incremental) with periodic full compaction; compaction and the final shutdown persist use a temp-file + atomic `File.Move(overwrite: true)` to avoid partial files.
- Retention (`AnalyticsStoreOptions.RetentionDays`) is enforced on both load and write; out-of-retention data found on load forces an immediate compaction.
- `PersistAllAsync` is re-entrancy guarded via `Interlocked` (`_persistInFlight`), so overlapping triggers coalesce; the fire-and-forget call from the ingest loop ignores results, so persist failures surface only as `LogWarning`.
