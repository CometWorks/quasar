# Magnetar.Protocol/Model/ViewerSceneDtos.cs

Shared metadata-only DTOs for the Quasar grid viewer scene contract. `EntityRenderSceneRequest` carries the target entity ID for `ServerCommandType.GetEntityRenderScene`; `EntityRenderScene` and related `Viewer*` classes describe grid identity, block placement, transforms, logical model/texture references, generated model parts, runtime subparts, bounds, chunks, and warnings without including raw assets or extracted mesh geometry.
