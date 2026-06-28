# Magnetar.Protocol/Model/ViewerSceneDtos.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Shared DTOs for the Quasar grid viewer scene contract. `EntityRenderSceneRequest` carries the target entity ID plus optional voxel-data and context-mode inclusion flags for `ServerCommandType.GetEntityRenderScene`; `EntityRenderScene` and related `Viewer*` classes describe primary/context grid identity, ownership metadata for blocks/chunks/lights, context bounds and counters, block placement, transforms, scene environment, selected-grid logistics nodes/edges/systems including conveyor-route path points, active mod asset roots, captured block and subpart light sources, logical model/texture references with optional mod root hints, generated model parts, runtime subparts, LCD surface text/image metadata, empty online/offline placeholder images, block-level offline-hidden LCD materials, voxel body metadata, optional bounded voxel content/material chunks, bounds, chunks, and warnings without including raw assets, inventory contents, server local mod paths, or server-rendered LCD texture bytes.
