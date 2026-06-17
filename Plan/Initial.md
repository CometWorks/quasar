# Quasar Grid Viewer Integration Plan

## Direction

Integrate the proof-of-concept viewer from `~/Documents/se-grid-render` into Quasar as a strict bring-your-own-assets viewer.

Quasar should not serve Space Engineers model files, texture files, or extracted model geometry. The live server/agent path should only provide a view-time scene snapshot: entity identity, grid/block placement, transforms, colors, model logical names, texture logical names, subpart references, and warnings. The browser must render from the user's locally selected Space Engineers `Content` folder.

## Key Decision

Strip out the proof-of-concept HTTP asset API in favor of sending model names and positions at view time.

Do not port these POC endpoints into Quasar:

- `/v1/models/{id}/mesh`
- `/v1/assets/{id}/raw`
- `/v1/textures/{id}`
- `/v1/catalog/assets`
- Other generic model/texture/asset-serving endpoints

Quasar still needs a narrow viewer data path to request the selected grid scene from a connected agent. This is a scene metadata API, not an asset API.

## User Flow

1. User opens `Entities` in Quasar.
2. Quasar shows an eye icon before each grid name.
3. User clicks the eye icon.
4. Quasar opens `/viewer/entity?agentId=...&entityId=...`.
5. Viewer asks Quasar for a scene snapshot for that agent/entity.
6. Quasar sends a command to `Quasar.Agent` over the existing agent WebSocket.
7. `Quasar.Agent` builds a metadata-only scene snapshot on the game thread.
8. Browser asks the user to select their Space Engineers `Content` folder.
9. Browser resolves `.mwm` models and texture files locally and renders the scene.
10. Missing local models/textures render as fallbacks with clear warnings.

## Quasar UI

- Add an eye viewer icon in `Quasar/Components/Pages/Entities.razor` before the grid name.
- Enable the icon for grid entities only.
- Open the viewer route with the selected `agentId` and `entityId`.
- Preserve existing entity actions such as delete.

## Quasar Web App

- Add a small viewer route under static assets, for example `Quasar/wwwroot/viewer`.
- Add a narrow endpoint such as `/api/viewer/entities/{agentId}/{entityId}/scene`.
- Protect the endpoint with the existing Quasar view authorization policy.
- Implement a service that sends a new agent command and returns the scene snapshot payload.
- Do not add raw asset or model mesh endpoints.

## Quasar.Agent

- House all plugin-side scene extraction code in `Quasar.Agent`.
- Add a command type such as `GetEntityRenderScene`.
- Execute scene extraction on the game thread through the existing `GameBridge` command flow.
- Return metadata only.

The agent may include:

- Grid entity ID, display name, world matrix, grid size, static/dynamic state, and bounds.
- Block IDs and definition IDs.
- Block cell coordinates, min/max cells, local transforms, orientation, color mask, skin ID, build level, integrity, and ownership metadata when useful.
- Model logical paths referenced by block definitions/current block state.
- Generated cube-part model logical paths and local transforms.
- Runtime subpart model logical paths and local transforms.
- Texture logical paths referenced by materials when available as metadata.
- Non-fatal warnings.

The agent must not include:

- Raw `.mwm` bytes.
- Raw texture bytes.
- Extracted model vertex/index/UV/normal arrays.
- Any generic asset download capability.

## Protocol

- Put shared viewer DTOs in `Magnetar.Protocol`.
- Keep the DTOs explicit and JSON-friendly.
- Serialize 64-bit entity IDs as strings if they are consumed directly by JavaScript.
- Keep the first contract focused on grids. Voxel support can be added later with the same no-asset-transfer rule.

## Viewer Asset Loading

- Replace POC network texture loading with local content-folder resolution.
- Replace POC network model mesh loading with browser-side local model loading.
- Use `showDirectoryPicker()` where available.
- Provide a practical fallback for browsers without directory picker support where possible.
- Store/reuse the selected folder handle when the browser permits it.
- Validate that the selected folder looks like a Space Engineers `Content` folder.
- Resolve paths case-insensitively where practical, since users may run Quasar from Linux while selecting content from platforms with different case behavior.

## Renderer Split

Split the proof-of-concept `Viewer/src/app.js` into smaller modules before integrating it into Quasar.

Suggested modules:

- `main.js`
- `state.js`
- `scene.js`
- `controls.js`
- `quasar-api.js`
- `content-folder.js`
- `mwm-loader.js`
- `texture-loader.js`
- `materials.js`
- `grid-renderer.js`
- `geometry.js`
- `math.js`
- `logging.js`

Remove or defer POC-only features that do not fit the Quasar integration:

- Vite proxy controls.
- POC API token fields.
- Raw asset download and texture ZIP debug buttons.
- Generic grid/voxel listing UI from the POC.
- Remote/WebRTC renderer path unless it becomes a separate requirement.

## Fallbacks

- If a model cannot be found locally, render a correctly placed bounding/proxy box.
- If a model loader is incomplete, use the same fallback and log the model logical path.
- If a texture cannot be found locally, use generated fallback materials.
- If the user has not selected a content folder, show a blocking prompt with a clear explanation.
- For modded grids, document that matching local mod content is required; otherwise affected blocks render as fallbacks.

## Documentation

- Update user documentation to explain the viewer and the local `Content` folder requirement.
- Document that Quasar does not transmit Space Engineers models/textures or extracted model geometry.
- Document mod content limitations and fallback behavior.
- Refresh generated reference documentation after code changes.

## Verification

- Build `Quasar.sln`.
- Verify the viewer static assets are served by Quasar.
- Verify the eye icon appears before grid names.
- Verify a connected agent returns a scene snapshot for a selected grid.
- Verify browser network activity does not include model files, texture files, or extracted model geometry.
- Verify local content-folder selection loads models/textures from disk.
- Verify missing assets fall back cleanly and report useful warnings.
