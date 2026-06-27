# Quasar/Services/ViewerSceneService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Quasar service that requests viewer scene snapshots from a connected agent using the existing command/result WebSocket pipeline. It sends `GetEntityRenderScene` with the target entity ID and optional voxel-mesh inclusion flag, waits for the correlated result, deserializes `EntityRenderScene`, and never exposes raw Space Engineers asset files or texture bytes.
