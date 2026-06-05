# Quasar/Services/Analytics/RrdCircularBuffer.cs

**Module:** Quasar.Services.Analytics  **Kind:** class  **Tier:** 2

## Summary

Fixed-capacity ring buffer of `MetricSample` values modelled after an RRD (round-robin database) circular archive. When full, new pushes overwrite the oldest slot. All mutations and reads are guarded by a single `lock` for thread safety. Rejects duplicate or out-of-order timestamps on `Push`.

## Structure

Namespace: `Quasar.Services.Analytics`

**`RrdCircularBuffer`** (sealed class)

Constructor:
- `RrdCircularBuffer(int capacity)` — allocates the fixed `MetricSample[]` array

Properties:
- `Capacity : int` — fixed slot count
- `Count : int` — current number of stored samples (lock-guarded)

Methods:
- `Push(in MetricSample) : bool` — appends sample; returns `false` if timestamp ≤ latest stored (deduplication); advances `_head` when at capacity
- `Read(long fromUnixSeconds, long toUnixSeconds) : MetricSample[]` — returns samples in the given time window (inclusive), in chronological order
- `ReadLatest(int n) : MetricSample[]` — returns the most-recent `n` samples (chronological order)
- `ReadAll() : MetricSample[]` — returns all current samples in chronological order
- `ReplaceAll(IReadOnlyList<MetricSample>)` — bulk-replaces content (used for disk restore); skips samples beyond capacity and enforces monotonic timestamps; resets `_head` to 0

Private:
- `GetAt(int offset) : MetricSample` — index-relative accessor using `(_head + offset) % capacity`

## Dependencies

- [`Quasar/Services/Analytics/MetricSample.cs`](MetricSample.cs.md)

## Notes

- All public methods that access `_slots`, `_head`, or `_count` acquire `_sync`. The `Count` property also locks.
- `ReplaceAll` does not call `Push`; it writes directly into `_slots` starting at index 0, so after restore `_head` is always 0 and old circular position is lost (acceptable for the restore path).
