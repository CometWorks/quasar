# Quasar/Services/Analytics/RrdRollupBuffer.cs

**Module:** Quasar.Services.Analytics  **Kind:** class  **Tier:** 2

## Summary

RRD-style consolidation buffer that accumulates raw `MetricSample` values into fixed-width time windows and pushes one averaged/max'd sample per window into an inner `RrdCircularBuffer`. Consolidation strategy: `SimSpeed`, `CpuPercent`, `MemoryMb`, and `FrameTimeMs` are averaged; `PlayersOnline` and `UsedPcu` are max'd; `ActiveGridCount` and `ActiveEntityCount` are averaged over samples where they are non-negative (-1 = unavailable).

## Structure

Namespace: `Quasar.Services.Analytics`

**`RrdRollupBuffer`** (sealed class)

Constructor:
- `RrdRollupBuffer(int capacity, int rollupIntervalSeconds)` — wraps a new `RrdCircularBuffer(capacity)`

Properties:
- `RollupIntervalSeconds : int`

Methods:
- `Observe(in MetricSample)` — determines the current window start (`timestamp - timestamp % interval`); if the window changes, flushes the previous window to the inner buffer and starts a new one; out-of-order samples (window < current) are silently dropped
- `Read(long from, long to) : MetricSample[]` — delegates to inner buffer
- `ReadLatest(int n) : MetricSample[]` — delegates to inner buffer
- `ReadAll() : MetricSample[]` — delegates to inner buffer
- `ReplaceAll(IReadOnlyList<MetricSample>)` — replaces inner buffer and resets the accumulator

Private accumulator state (all reset by `ResetAccumulator`):
- `_hasWindow`, `_windowStartUnixSeconds`, `_sampleCount`
- `_sumSimSpeed`, `_sumCpuPercent`, `_sumMemoryMb`, `_sumFrameTimeMs`
- `_maxPlayersOnline`, `_maxUsedPcu`
- `_sumActiveGridCount` / `_activeGridCountSamples`, `_sumActiveEntityCount` / `_activeEntityCountSamples`

`FlushWindow` emits the consolidated `MetricSample` at `_windowStartUnixSeconds` and pushes it via `_buffer.Push`.

## Dependencies

- [`Quasar/Services/Analytics/RrdCircularBuffer.cs`](RrdCircularBuffer.cs.md)
- [`Quasar/Services/Analytics/MetricSample.cs`](MetricSample.cs.md)

## Notes

- `Observe` acquires `_sync` for the entire window-check-and-accumulate path; `_buffer` handles its own lock internally.
- `ActiveGridCount` / `ActiveEntityCount` use separate sample counters so that windows where the agent did not report these fields (value = -1) do not skew the average. A window where none of the samples reported them yields -1 in the consolidated output.
- The current open window is never flushed until the next sample from a later window arrives; the last window before shutdown is therefore lost unless `MetricsStoreService.PersistAllAsync` calls `ReadAll` (which reads only fully flushed windows from the inner buffer).
