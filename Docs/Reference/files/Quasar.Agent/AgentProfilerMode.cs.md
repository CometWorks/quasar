# Quasar.Agent/AgentProfilerMode.cs

**Module:** Quasar.Agent  **Kind:** enum  **Tier:** 1

## Summary

Public enum describing the in-DS profiler patch depth selected through `AgentOptions.ProfilerMode`.

## Structure

Namespace: `Quasar.Agent`

**`AgentProfilerMode`** (`public enum`)

Values:
- `Off` - disables profiler collection and Harmony profiler patches.
- `SafeContinuous` - default; enables continuous low-overhead high-level method patches without deep IL call-site transpilers or broad entity update patching.
- `DeepContinuous` - enables continuous method patches plus deep call-site IL transpilers.

## Dependencies

None.

## Notes

`AgentOptions.FromEnvironment()` reads this mode from `QUASAR_AGENT_PROFILER_MODE`; managed servers normally receive the per-server `DedicatedServerDefinition.AgentProfilerMode`, with `WebServiceOptions.AgentProfilerMode` only as the fallback.
