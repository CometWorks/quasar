# Quasar/wwwroot/viewer/quasar-api.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
HTTP helper for the standalone grid viewer. It reads the `agentId`, `entityId`, optional `voxels`, and optional `context` query parameters, parses true-like/false-like support state, and fetches scene JSON from Quasar's viewer API with explicit `voxels=...` and `context=...` request flags.

## Structure

| Export | Purpose |
|---|---|
| `getViewerParams()` | Parses and validates `agentId`/`entityId` from `window.location.search` and includes parsed voxel/context support state. |
| `parseContextFlag()` | Returns whether the URL explicitly contains `context` and whether the value is true-like. |
| `parseVoxelFlag()` | Returns whether the URL explicitly contains `voxels` and whether the value is true-like. |
| `fetchEntityScene()` | Performs a same-origin JSON fetch to `/api/viewer/entities/{agentId}/{entityId}/scene?voxels=...&context=...` and unwraps problem details on failure. |

## Dependencies
- Browser `URLSearchParams` and `fetch` APIs.
- The Quasar viewer scene endpoint implemented by the web host.
