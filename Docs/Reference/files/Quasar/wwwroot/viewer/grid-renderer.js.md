# Quasar/wwwroot/viewer/grid-renderer.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Main renderer for grid, context-grid, and asteroid viewer scenes. It computes selected-grid floor alignment, creates one relative-view group per exported grid, renders toggleable logistics and damaged-block overlays with through-wall model masks/fallback masks, batches/proxies blocks under their owning grid transform, builds capped in-scene light groups, resolves local Space Engineers models/textures from Content and Mods folders, applies Space Engineers-style paint/transparency/LCD behavior, progressively rebuilds per-grid model layers while assets load, and procedurally meshes voxel content/material chunks when present.

## Structure

| Export | Purpose |
|---|---|
| `renderGridScene(scene)` | Replaces the active grid/voxel scene, computes floor-grid footprint alignment, resolves models, initializes progressive texture stats, creates batches/proxies, updates bounds/camera, and refreshes summary/stat fields. |

Internal sections cover selected-grid-relative transforms, context bounds, per-grid grouping, logistics overlay construction, damaged-block overlay construction from `BuildLevel`/`Integrity`/`MaxIntegrity`, overlay stats, depth-test-disabled overlay masks/tracers, captured point/spot light creation, projected spot-light shadow selection, proxy-first rendering, local armor skin and transparent material definition parsing, delayed model layer rebuilds, voxel terrain generation, block model transforms, shared geometry/material caches, LCD material replacement, paint/color-mask shader injection, instanced batch flushing, bounded-concurrency model resolution, texture metadata collection, and progressive texture loading.

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
Logistics masks and damaged-block masks render through walls and are rebuilt alongside progressive model-layer rebuilds so they upgrade from fallback boxes to model-shaped masks as local MWM assets become available. Damaged-block intensity is based on game-style current damage, `BuildLevel * MaxIntegrity - Integrity`, so unfinished intact blocks are not counted as damaged.
