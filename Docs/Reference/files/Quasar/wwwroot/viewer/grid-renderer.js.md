# Quasar/wwwroot/viewer/grid-renderer.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Main renderer for metadata-only grid scenes. It displays instanced proxy batches while local MWM models resolve with bounded parallelism, progressively rebuilds the model layer with adaptive throttling, builds instanced batches for shared model/material combinations, adds voxel proxies and lighting context, and progressively loads selected material textures without blocking scene construction.

## Structure

| Export | Purpose |
|---|---|
| `renderGridScene(scene)` | Replaces the active grid/voxel scene, resolves models, initializes progressive texture stats, creates batches/proxies, updates bounds/camera, and refreshes summary/stat fields. |

Internal sections cover relative grid-view transforms, immediate proxy-first rendering, adaptively delayed model layer rebuilds for larger scenes, instanced proxy batching, voxel proxy rendering, model-part/subpart mesh creation, shared geometry/material caches per rebuild, Space Engineers-style paint/color-mask shader injection, instanced batch flushing, bounded-concurrency model resolution, texture metadata collection, and progressive texture load status tracking.

## Dependencies
- `three`.
- [`Quasar/wwwroot/viewer/state.js`](state.js.md) for renderer state, scene objects, stats, and caches.
- [`Quasar/wwwroot/viewer/geometry.js`](geometry.js.md), [`Quasar/wwwroot/viewer/materials.js`](materials.js.md), and [`Quasar/wwwroot/viewer/math.js`](math.js.md) for low-level rendering helpers.
- [`Quasar/wwwroot/viewer/scene.js`](scene.js.md) for disposal, fitting, lighting, bounds, and sun-position updates.
- [`Quasar/wwwroot/viewer/mwm-loader.js`](mwm-loader.js.md) for local model resolution/parsing.
- [`Quasar/wwwroot/viewer/texture-loader.js`](texture-loader.js.md) for coalesced progressive texture loading.
- [`Quasar/wwwroot/viewer/logging.js`](logging.js.md) for non-fatal warning/fallback messages.

## Notes
Texture counts start from scene/model metadata, then found/missing/loaded/failed counters update as visible/shared materials request textures. Unresolved block proxies are emitted as a small number of instanced batches grouped by opacity rather than one proxy mesh per block, which reduces the temporary draw-call cost on large grids while preserving per-block hover metadata through `InstancedMesh.userData.blocks`. Model resolution runs with a wider worker cap, and progressive rebuilds wait for a threshold of completed models or a scene-size-dependent timeout before rebuilding.
