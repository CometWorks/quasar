# Quasar/wwwroot/viewer/grid-renderer.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Main renderer for grid, context-grid, and asteroid viewer scenes. It computes selected-grid floor alignment, clips context-grid models, logistics masks, damaged-block masks, and voxel terrain to the selected primary grid footprint, renders toggleable logistics and damage overlays, batches/proxies blocks under their owning grid transform, applies current-model matrices and damaged skeleton deformation, builds capped in-scene light groups with projected-shadow budgeting, resolves local Space Engineers models/textures from Content and Mods folders, resolves armor skin and transparent material definitions, applies Space Engineers-style paint/transparency/LCD behavior and captured status-emissive material overrides as visual glow only rather than light sources, preloads model/LCD/voxel textures before swapping in a reloaded scene, and procedurally meshes voxel content/material chunks when present.

## Structure

| Export | Purpose |
|---|---|
| `renderGridScene(scene, options)` | Prepares a replacement grid/voxel scene by computing floor-grid footprint alignment, resolving models, reporting load progress, preloading discovered textures, creating batches/proxies, swapping the prepared groups into the active Three.js scene, updating bounds/camera, and refreshing summary/stat fields. |

Internal sections cover selected-grid-relative transforms, context bounds, shared floor-rectangle clipping helpers, per-grid grouping, logistics overlay construction with context-grid polyline clipping after offline midpoint splitting, damage overlay construction using the projector block's `BuildLevel`/`AccumulatedDamage`/current-damage predicate plus voxel damage chunks filtered by modified sample masks and grouped separately so voxel visibility can hide deformation masks, overlay stats, depth-test-disabled overlay masks/tracers, captured point/spot light creation, projected spot-light shadow selection, prepared scene swapping, local armor skin and transparent material definition parsing, voxel terrain generation, block model transforms, shared geometry/material caches, LCD material replacement, paint/color-mask shader injection, instanced batch flushing, bounded-concurrency model resolution, texture metadata collection, and preload-backed texture binding.

## Dependencies
- `three`.
- [`Quasar/wwwroot/viewer/state.js`](state.js.md) for renderer state, scene objects, stats, and caches.
- [`Quasar/wwwroot/viewer/geometry.js`](geometry.js.md), [`Quasar/wwwroot/viewer/materials.js`](materials.js.md), and [`Quasar/wwwroot/viewer/math.js`](math.js.md) for low-level rendering helpers.
- [`Quasar/wwwroot/viewer/scene.js`](scene.js.md) for disposal, fitting, lighting, bounds, and sun-position updates.
- [`Quasar/wwwroot/viewer/mwm-loader.js`](mwm-loader.js.md) for local model resolution/parsing.
- [`Quasar/wwwroot/viewer/texture-loader.js`](texture-loader.js.md) for coalesced progressive texture loading.
- [`Quasar/wwwroot/viewer/content-folder.js`](content-folder.js.md) for mod-root registration and local SBC resolution.
- [`Quasar/wwwroot/viewer/lcd-font-loader.js`](lcd-font-loader.js.md) for local Space Engineers LCD bitmap font loading and canvas glyph drawing.
- [`Quasar/wwwroot/viewer/logging.js`](logging.js.md) for non-fatal warning/fallback messages.

## Notes
Logistics masks and damaged-block masks render through walls and are rebuilt alongside progressive model-layer rebuilds so they upgrade from fallback boxes to model-shaped masks as local MWM assets become available. In context mode, masks for boundary-crossing context blocks reuse the clipped block geometry path and logistics lines are clipped in viewer-relative X/Z while fully-inside context block masks and primary-grid overlays remain on the normal unclipped path. Damaged-block detection matches the projector marker logic: partially built blocks, blocks with accumulated damage, and blocks with positive current damage are highlighted. Voxel damage masks render from `voxelDamageDeformations`, only polygonize terrain adjacent to changed samples, not whole voxel bodies or every visible terrain chunk, and are hidden when `Show voxels` is unchecked.
