# Quasar.Agent/EmissivePartCapturePatches.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
Harmony patch registrar for grid-viewer status emissive capture. It patches `MyEntity.SetEmissiveParts`, `MyEntity.UpdateNamedEmissiveParts`, `MyCubeBlock.SetEmissiveState`, `MyCubeBlock.UpdateEmissiveParts`, and the private segmented emissive helpers used by batteries and oxygen/hydrogen tanks so the agent records game-resolved named material colors and emissivity without calling client render APIs. Patches are applied by `AdminPlugin` at plugin startup and removed during disposal.
