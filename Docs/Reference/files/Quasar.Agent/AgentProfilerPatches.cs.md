# Quasar.Agent/AgentProfilerPatches.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary

Harmony patch registrar for Quasar's profiler telemetry. It patches known Space Engineers server methods for frame/update, programmable block scripts, physics, replication, network, and session components, then scans loaded entity types for declared simulation update methods so grids and other entities can be timed.

## Structure

Namespace: `Quasar.Agent`

**`AgentProfilerPatches`** (`internal static class`)

Key members:
- `Apply()` — creates the Harmony instance, applies all profiler patches, and logs the number of successfully patched targets.
- `Dispose()` — unpatches Quasar profiler patches by Harmony id.
- `PatchKnownMethods()` — patches named game/server methods found by type/name and returns the success count; inherited methods are resolved to the actual declaring type before Harmony sees them.
- `PatchEntityUpdateMethods()` — patches update methods declared by entity-derived types and returns the success count.
- `Patch(...)` / `PatchDeclared(...)` / `PatchDeclaredByTypeName(...)` — isolate individual Harmony failures, log skipped targets, and keep patching the rest.
- `Prefix` / `InstancePostfix` / `StaticPostfix` — call `AgentProfiler.Begin/End`.

## Dependencies

- [`Quasar.Agent/AgentProfiler.cs`](AgentProfiler.cs.md)
- `Lib.Harmony` / `HarmonyLib`
- Space Engineers assemblies: `Sandbox`, `Sandbox.Game.Entities`, `Sandbox.Game.Entities.Blocks`

## Notes

Static and instance postfixes are separate so Harmony does not request `__instance` for static targets. Known-method lookup walks base types with `DeclaredOnly` binding flags so inherited API moves, such as `RunSingleFrame` living on `Sandbox.Engine.Platform.Game` instead of `MySandboxGame`, do not produce Harmony's "patch the declared method" failure. Missing target methods and individual patch failures are ignored after logging, allowing the agent to survive minor game-build differences while still profiling any targets that patched successfully.
