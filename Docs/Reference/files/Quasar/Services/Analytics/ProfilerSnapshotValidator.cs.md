# Quasar/Services/Analytics/ProfilerSnapshotValidator.cs

**Module:** Quasar.Services.Analytics  **Kind:** class  **Tier:** 2

## Summary

Internal validation and normalization gate for profiler payloads received from agents. It rejects structurally unusable snapshots, clamps impossible frame/window/timing values, trims strings, bounds top-entry lists, and recomputes per-frame timings from totals and frame count.

## Structure

Namespace: `Quasar.Services.Analytics`

**`ProfilerSnapshotValidator`** (`internal static class`)

Key member:
- `TryNormalize(ProfilerSnapshot snapshot, out ProfilerSnapshot normalized) : bool`

Private helpers normalize timing breakdowns, profiler entries, bounded integers, finite doubles, and display strings.

## Dependencies

- [`Magnetar.Protocol/Model/ProfilerSnapshot.cs`](../../../Magnetar.Protocol/Model/ProfilerSnapshot.cs.md)
- [`Quasar/Services/Analytics/ProfilerStoreService.cs`](ProfilerStoreService.cs.md)

## Notes

The validator is the telemetry data-quality check before profiler data enters UI/API memory.
