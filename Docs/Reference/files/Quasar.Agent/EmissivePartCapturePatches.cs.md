# Quasar.Agent/EmissivePartCapturePatches.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
Harmony patch registrar for grid-viewer emissive capture. It patches `MyEntity.SetEmissiveParts`, `MyEntity.UpdateNamedEmissiveParts`, `MyCubeBlock.SetEmissiveState`, `MyCubeBlock.UpdateEmissiveParts`, `MyRenderProxy.UpdateModelProperties`, and the private segmented emissive helpers used by batteries and oxygen/hydrogen tanks so the agent records game-resolved named material colors and emissivity without serving render assets. Generic red/green cube-block status captures are skipped for lighting blocks; their bulb materials are captured from the game's render-property material updates instead. Patches are applied by `AdminPlugin` at plugin startup and removed during disposal.
