# Quasar.Agent/GridRenderSceneInspector.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
Game-thread helper that builds viewer scene snapshots from live `MyCubeGrid` and asteroid `MyVoxelBase` instances. It maps primary and optional context grids into `Magnetar.Protocol` viewer DTOs, computes selected-grid-local context bounds plus matching relative and enclosing world AABBs, enumerates loaded intersecting grids deterministically, clips surrounding grids at block selection time with oriented context bounds and conservative context caps, registers active scene mod asset roots and logical model/texture names, includes generated cube-part and runtime subpart metadata, captures block build level, integrity, max integrity, and accumulated damage, captures selected-grid conveyor/logistics topology, captures grid lighting and LCD metadata, assigns grid ownership on blocks/chunks/lights, and samples bounded voxel content/material ranges when requested.
