# Quasar.Agent/EmissivePartCapturePatches.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
Harmony patch registrar for grid-viewer status emissive capture. It patches `MyEntity.SetEmissiveParts`, `MyEntity.UpdateNamedEmissiveParts`, `MyCubeBlock.SetEmissiveState`, and `MyCubeBlock.UpdateEmissiveParts` so the agent records game-resolved named material colors and emissivity without calling client render APIs or reimplementing per-block status logic. Patches are applied by `AdminPlugin` at plugin startup and removed during disposal.
