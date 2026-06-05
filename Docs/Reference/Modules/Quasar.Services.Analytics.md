# Quasar.Services.Analytics — Metrics Storage

*Module `Quasar.Services.Analytics` — 8 files.* See the [handbook TOC](../TOC.md) and the [file Index](../Index.md).

A lightweight round-robin-database (RRD) style metrics pipeline. `MetricsStoreService` (hosted) ingests `MetricSample`s derived from agent snapshots through a bounded, drop-oldest channel and appends new points to a compact JSONL file. Per server, `ServerMetricsStore` keeps three consolidation tiers with retention-sized rollups (`1 minute * retentionDays`, `1 hour * retentionDays`) built on `RrdCircularBuffer` and `RrdRollupBuffer`. `AnalyticsViewConfig` captures the dashboard's per-panel layout preferences.

## Files

| File | Kind | Summary |
| --- | --- | --- |
| [Quasar/Services/Analytics/AnalyticsViewConfig.cs](../files/Quasar/Services/Analytics/AnalyticsViewConfig.cs.md) | class | Defines three plain-data types that capture the user's analytics dashboard preferences: which time range to display, grid layout dimensions, data-tier selection mode, selected metric names, and per-panel visibility and ordering. These objects are serialised into user/session state by the analytics Blazor page and are not persisted to disk by the analytics service itself. |
| [Quasar/Services/Analytics/MetricSample.cs](../files/Quasar/Services/Analytics/MetricSample.cs.md) | struct | Immutable value type representing one point-in-time snapshot of a dedicated server's performance counters. Used as the storage unit inside `RrdCircularBuffer` and `RrdRollupBuffer`. All fields are readonly to keep the struct safe for `in` parameter passing. |
| [Quasar/Services/Analytics/MetricSampleFactory.cs](../files/Quasar/Services/Analytics/MetricSampleFactory.cs.md) | class | Internal static factory that converts an `AgentSnapshot` (received over the WebSocket from the agent) into a `MetricSample` ready for storage. Derives `FrameTimeMs` from `SimSpeed` because the protocol does not carry frame time directly. |
| [Quasar/Services/Analytics/AnalyticsStoreOptions.cs](../files/Quasar/Services/Analytics/AnalyticsStoreOptions.cs.md) | class | Stores analytics retention policy and helper accessors for retention and buffer capacity. |
| [Quasar/Services/Analytics/MetricsStoreService.cs](../files/Quasar/Services/Analytics/MetricsStoreService.cs.md) | class | `IHostedService` that owns the per-server metric stores, drives the single-reader ingest loop via a bounded `Channel`, and persists samples to append-only JSONL with retention-aware checkpointing and optional compaction. On startup it loads previously saved data from disk; on shutdown it drains the channel and performs a final persist. |
| [Quasar/Services/Analytics/RrdCircularBuffer.cs](../files/Quasar/Services/Analytics/RrdCircularBuffer.cs.md) | class | Fixed-capacity ring buffer of `MetricSample` values modelled after an RRD (round-robin database) circular archive. When full, new pushes overwrite the oldest slot. All mutations and reads are guarded by a single `lock` for thread safety. Rejects duplicate or out-of-order timestamps on `Push`. |
| [Quasar/Services/Analytics/RrdRollupBuffer.cs](../files/Quasar/Services/Analytics/RrdRollupBuffer.cs.md) | class | RRD-style consolidation buffer that accumulates raw `MetricSample` values into fixed-width time windows and pushes one averaged/max'd sample per window into an inner `RrdCircularBuffer`. Consolidation strategy: `SimSpeed`, `CpuPercent`, `MemoryMb`, and `FrameTimeMs` are averaged; `PlayersOnline` and `UsedPcu` are max'd; `ActiveGridCount` and `ActiveEntityCount` are averaged over samples where they are non-negative (-1 = unavailable). |
| [Quasar/Services/Analytics/ServerMetricsStore.cs](../files/Quasar/Services/Analytics/ServerMetricsStore.cs.md) | class | Holds the three-tier RRD-style metric history for one dedicated server: raw ring plus retention-sized rollups (`RetentionDays * 24 * 60` and `RetentionDays * 24` slots). Owned and managed by `MetricsStoreService`; not thread-safe on its own — callers are responsible for serialisation. |

## Depends on

- [Quasar.Services.Core](Quasar.Services.Core.md)
