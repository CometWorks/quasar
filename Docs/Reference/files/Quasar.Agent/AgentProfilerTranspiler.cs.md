# Quasar.Agent/AgentProfilerTranspiler.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary

Generic Harmony `CodeInstruction` transpiler used by deep profiler mode. It wraps selected `call` / `callvirt` instructions with `AgentProfiler.BeginCallSite` and `AgentProfiler.EndCallSite`, giving Quasar-native call-site attribution without external patch-manager MSIL helpers.

## Structure

Namespace: `Quasar.Agent`

**`AgentProfilerTranspiler`** (`internal static class`)

Key members:
- `CreateCandidate(Type declaringType, string methodNameRegex, string category)` - creates a call-site matcher for a target method family.
- `Patch(Harmony harmony, MethodBase original, string patchName, IEnumerable<Candidate> candidates)` - stores matchers for an original method and applies the shared transpiler.
- `Transpile(...)` - scans IL, instruments matching calls, and logs when a patch target had no matching call sites.
- `Candidate.Matches(MethodBase)` - matches by declaring type assignability and method-name regex.

## Dependencies

- [`Quasar.Agent/AgentProfiler.cs`](AgentProfiler.cs.md)
- [`Quasar.Agent/AgentProfilerPatches.cs`](AgentProfilerPatches.cs.md)
- `HarmonyLib`
- BCL reflection/IL types

## Notes

The transpiler is deliberately conservative. It skips open generic calls, by-ref/pointer parameters, by-ref/pointer returns, value-type instance calls, and calls with CLR prefix opcodes so failed or unsafe IL patterns do not risk the agent.
