# Quasar/wwwroot/viewer/grid-renderer.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Main renderer for metadata-only grid scenes. It displays instanced proxy batches while local MWM models resolve with bounded parallelism, resolves local armor skin texture overrides from `ArmorModifiers.sbc`, resolves glass and transparent LCD screen-area material names through `TransparentMaterials.sbc`, conditionally applies LCD screen material overrides for active content, built-in empty online/offline placeholders, model-preserving `ContentType.NONE` surfaces, and reset-to-model `ContentType.NONE` surfaces whose non-transparent screen material group is hidden like the game, skips block-definition materials hidden while LCD blocks are offline, renders transparent LCD replacements single-sided and in the late transparent render layer to match the game material path and keep the floor grid consistently tinted, composes LCD background colors with regular/transparent-screen semantics, lays out LCD canvases in Space Engineers `SurfaceSize` coordinates before mapping them to the raw render texture, decodes LCD sprite images with premultiplied-atlas alpha recovery for built-in wide placeholders, renders loaded LCD text through the local Space Engineers bitmap font loader while leaving the temporary Canvas2D fallback unwrapped so overflow clips like the game, logs LCD surface selection/texture/draw decisions into the viewer log for troubleshooting, progressively rebuilds the model layer with adaptive throttling, builds instanced batches for shared model/material combinations, applies Space Engineers-style material paint while skipping transparent glass/screen planes, handles cutout and blended transparency, adds voxel proxies and lighting context, and progressively loads selected material textures without blocking scene construction.

## Structure

| Export | Purpose |
|---|---|
| `renderGridScene(scene)` | Replaces the active grid/voxel scene, resolves models, initializes progressive texture stats, creates batches/proxies, updates bounds/camera, and refreshes summary/stat fields. |

Internal sections cover relative grid-view transforms, immediate proxy-first rendering, local armor skin and transparent material definition parsing, adaptively delayed model layer rebuilds for larger scenes, painted instanced proxy solids with batched cuboid edge outlines, voxel proxy rendering, full-matrix block model transforms with legacy `ModelOffset` fallback, model-part/subpart mesh creation, shared geometry/material caches per rebuild including game-oriented MWM UVs with cube-part atlas offsets and skin-aware/material-keyed transparent texture data, active LCD material replacement using direct local textures or generated canvas text/sprite/background composition on depth-tested screen geometry while omitting matching non-transparent `ScreenArea*` base material groups, vanilla `SurfaceSize` LCD layout mapping for non-square screen aspects, asynchronous LCD bitmap font loading/rerendering for text surfaces and programmable-block text sprites, single-sided transparent LCD replacement rendering, late render ordering for transparent LCD batches so the floor grid blends behind them consistently, default transparent rendering for inactive transparent LCD rotation planes, Space Engineers-style paint/color-mask shader injection for paintable mesh parts, transparent glass/screen paint suppression, alpha-mask cutout discard for `ALPHA_MASKED`/`DECAL_CUTOUT` materials, depth-write-disabled blended rendering for glass/holo/shield materials, instanced batch flushing, bounded-concurrency model resolution, texture metadata collection, and progressive texture load status tracking.

## Dependencies
- `three`.
- [`Quasar/wwwroot/viewer/state.js`](state.js.md) for renderer state, scene objects, stats, and caches.
- [`Quasar/wwwroot/viewer/geometry.js`](geometry.js.md), [`Quasar/wwwroot/viewer/materials.js`](materials.js.md), and [`Quasar/wwwroot/viewer/math.js`](math.js.md) for low-level rendering helpers.
- [`Quasar/wwwroot/viewer/scene.js`](scene.js.md) for disposal, fitting, lighting, bounds, and sun-position updates.
- [`Quasar/wwwroot/viewer/mwm-loader.js`](mwm-loader.js.md) for local model resolution/parsing.
- [`Quasar/wwwroot/viewer/texture-loader.js`](texture-loader.js.md) for coalesced progressive texture loading.
- [`Quasar/wwwroot/viewer/content-folder.js`](content-folder.js.md) for local `ArmorModifiers.sbc` resolution.
- [`Quasar/wwwroot/viewer/lcd-font-loader.js`](lcd-font-loader.js.md) for local Space Engineers LCD bitmap font loading and canvas glyph drawing.
- [`Quasar/wwwroot/viewer/logging.js`](logging.js.md) for non-fatal warning/fallback messages.

## Notes
Texture counts start from scene/model metadata, then found/missing/loaded/failed counters update as visible/shared materials request textures. Unresolved block proxies are emitted as a small number of instanced solid batches and line-edge batches grouped by opacity rather than one proxy mesh per block, which reduces the temporary draw-call cost on large grids while preserving per-block hover metadata through `InstancedMesh.userData.blocks`. Model resolution runs with a wider worker cap, and progressive rebuilds wait for a threshold of completed models or a scene-size-dependent timeout before rebuilding.
