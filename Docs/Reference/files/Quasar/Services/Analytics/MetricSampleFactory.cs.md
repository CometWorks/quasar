# Quasar/Services/Analytics/MetricSampleFactory.cs

**Module:** Quasar.Services.Analytics  **Kind:** class  **Tier:** 2

## Summary

Internal static factory that converts an `AgentSnapshot` (received over the WebSocket from the agent) into a `MetricSample` ready for storage. Derives `FrameTimeMs` from `SimSpeed` because the protocol does not carry frame time directly.

## Structure

Namespace: `Quasar.Services.Analytics`

**`MetricSampleFactory`** (internal static class)

- `FromSnapshot(AgentSnapshot snapshot) : MetricSample` — maps `snapshot.Metrics` fields to a `MetricSample`; computes `frameTimeMs = 1000 / (simSpeed * 60)` when `SimSpeed > 0.001`, otherwise 0; maps `null` optional fields to sentinel values (`0` for memory, `-1` for grid/entity counts).

## Dependencies

- [`Quasar/Services/Analytics/MetricSample.cs`](MetricSample.cs.md)
- `Magnetar.Protocol.Model.AgentSnapshot` (external package `Magnetar.Protocol`)

## Notes

`ActiveGridCount` and `ActiveEntityCount` are nullable in the protocol and default to `-1` when absent, matching the sentinel convention used by `RrdRollupBuffer.Accumulate`.
