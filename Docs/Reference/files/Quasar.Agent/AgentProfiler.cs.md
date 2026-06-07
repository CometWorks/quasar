# Quasar.Agent/AgentProfiler.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary

Low-duty in-process profiler accumulator used by Harmony patches. It starts its first 10 second sample window on the first game-thread update, repeats every 60 seconds by default, records elapsed ticks from patched server methods, splits main-thread vs off-thread time, and publishes the latest `ProfilerSnapshot` for inclusion in the next agent snapshot.

## Structure

Namespace: `Quasar.Agent`

**`AgentProfiler`** (`internal static class`)

Key members:
- `Active` — true while a sample window is open.
- `MarkGameThread()` — records current game-thread id.
- `Update()` — starts/finishes sample windows from the game tick.
- `Begin()` / `End(...)` — lightweight patch hooks using `Stopwatch.GetTimestamp`.
- `GetLatestSnapshot()` — returns latest completed profiler window.

Private helpers build game-loop breakdowns and top lists for grids, scripts, per-entity updates, physics, network/replication/session, and other system methods.

## Dependencies

- [`Quasar.Agent/ProfilerDescriptor.cs`](ProfilerDescriptor.cs.md)
- [`Magnetar.Protocol/Model/ProfilerSnapshot.cs`](../Magnetar.Protocol/Model/ProfilerSnapshot.cs.md)
- `Sandbox.MySandboxGame` — simulation frame counter

## Notes

Sampling is intentionally intermittent and top-list bounded to keep default telemetry useful without turning every managed server into a full-time deep profiler. The first window starts immediately once `GameBridge.Update` begins ticking.
