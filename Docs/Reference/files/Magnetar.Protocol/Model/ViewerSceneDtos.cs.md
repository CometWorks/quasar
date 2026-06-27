# Magnetar.Protocol/Model/ViewerSceneDtos.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Shared DTOs for the Quasar grid viewer scene contract. `EntityRenderSceneRequest` carries the target entity ID and optional voxel-data inclusion flag for `ServerCommandType.GetEntityRenderScene`; `EntityRenderScene` and related `Viewer*` classes describe grid identity, block placement, transforms, scene environment, captured block and subpart light sources, logical model/texture references, generated model parts, runtime subparts, LCD surface text/image metadata, empty online/offline placeholder images, block-level offline-hidden LCD materials, voxel body metadata, optional bounded voxel content/material chunks, bounds, chunks, and warnings without including raw assets or server-rendered LCD texture bytes.
