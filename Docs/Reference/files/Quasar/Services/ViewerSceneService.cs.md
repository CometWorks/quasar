# Quasar/Services/ViewerSceneService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Quasar service that requests viewer scene snapshots from a connected agent using the existing command/result WebSocket pipeline. It sends `GetEntityRenderScene`, waits for the correlated result, deserializes the metadata-only `EntityRenderScene`, and never exposes raw Space Engineers assets or mesh data.
