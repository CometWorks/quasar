# Quasar/wwwroot/viewer/scene.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Three.js scene, camera, lighting, controls, animation, interaction, and render-stat runtime for the standalone grid/asteroid viewer. It owns renderer setup, shadow-map configuration, orbit/free-fly camera behavior, scene-bound floor grid and default-hidden clipping wireframe generation after content is loaded, relative-space bounds tracking, context-mode selected-grid camera/sun-target anchoring with expanded context floor and shadow footprint, ambient/sun/grid lighting application, separate default-hidden sun marker helper visibility, object and viewer teardown disposal, viewport resizing, block/voxel/logistics/damage hover readouts, logistics hover focus, same-grid damaged-block hover focus, same-body voxel damage hover focus, optional FPS overlay updates, and per-frame stats rendering.

## Structure

Key exports:

| Export | Purpose |
|---|---|
| `SMALL_GRID_CUBE_SIZE` / `LARGE_GRID_CUBE_SIZE` / `ASTEROID_GRID_CUBE_SIZE` | Shared floor-grid sizing constants for small-grid, large-grid, and asteroid-scale viewer modes. |
| `initScene()` | Creates the scene, renderer, camera, controls, lighting, pointer handlers, and resize observer; the floor grid is created later from loaded scene bounds. |
| `animate(time)` | Per-frame render loop that updates controls/free-fly movement, renders the scene, refreshes the optional FPS overlay, and refreshes render stats. Three.js updates any `LOD.autoUpdate` objects during rendering. |
| `disposeViewer()` | Stops animation and observers, removes event handlers, disposes the scene tree, cached textures, controls, renderer, and WebGL context, and detaches the canvas. |
| `replaceFloorGrid(bounds, gridSize, alignment = null)` | Rebuilds the scaled floor grid using SE small/large/asteroid grid cell semantics, optional per-axis lattice offsets, refreshes the clipping wireframe bounds, and updates fog density from the floor span. |
| `setClippingVisible(visible)` | Toggles the light-blue clipping wireframe helper shown by the `Show Clipping` render checkbox. |
| `updateFpsOverlay(now = performance.now())` | Shows, hides, and periodically refreshes the top-right FPS overlay behind the `Show FPS` checkbox. |
| `floorGridLayout(bounds, gridSize, alignment)` | Computes the snapped floor-grid cell layout used by both floor rendering and client-side voxel clipping. |
| `fitCameraToScene()` | Frames the active grid bounds and updates camera clipping planes/orbit target. |
| `updateSceneBounds(refit = false)` | Recomputes displayed relative-space bounds and refreshes the floor grid and sun marker placement. |
| `updateLighting()` | Applies the lighting toggle state to ambient fill, the directional sun, and grid light group, and independently applies the separate sun marker toggle to the marker and ray. |
| `updateSunLightPosition()` | Positions the directional sun, selected-grid target, marker, ray indicator, and floor/clipping-footprint fitted orthographic shadow camera in relative-grid space. |
| `disposeObjectTree(root)` | Disposes geometries, materials, and non-cached material-owned textures below a scene object. |
| `collectObjectTreeTextures(root, textures = new Set())` | Collects material and shader-uniform textures referenced below a scene object for cache pruning. |
| `setCameraMode(mode)` | Switches between orbit and free-fly camera modes. |

## Dependencies
- `three` and `three/addons/controls/OrbitControls.js`.
- [`Quasar/wwwroot/viewer/state.js`](state.js.md) for global viewer runtime state and cached DOM elements.
- [`Quasar/wwwroot/viewer/geometry.js`](geometry.js.md) for bounds conversion.
- [`Quasar/wwwroot/viewer/content-folder.js`](content-folder.js.md) for cached texture disposal during full viewer teardown.

## Notes
The hover path prioritizes visible overlay intersections before regular block/voxel readouts and ignores hidden mutually exclusive overlays and hidden overlay subgroups. Damage focus brightens damaged block masks on the hovered block's grid or modified voxel damage chunks from the hovered voxel body, and dims other visible damage masks. Object disposal frees generated per-scene textures such as LCD canvas textures, but skips textures still owned by the shared viewer texture cache so cache entries are not left pointing at disposed GPU resources. Scene reload cache pruning uses collected active-scene texture references to dispose cached textures from previous scenes. Full viewer teardown separately disposes the shared texture cache before disposing and losing the renderer context.
