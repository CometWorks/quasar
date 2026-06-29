# Quasar/wwwroot/viewer/scene.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Three.js scene, camera, lighting, controls, animation, interaction, and render-stat runtime for the standalone grid/asteroid viewer. It owns renderer setup, shadow-map configuration, orbit/free-fly camera behavior, floor grid generation, relative-space bounds tracking, context-mode selected-grid floor/camera/sun anchoring, ambient/sun/grid lighting application, object disposal, viewport resizing, block/voxel/logistics/damage hover readouts, logistics hover focus, same-grid damaged-block hover focus, same-body voxel damage hover focus, and per-frame stats rendering.

## Structure

Key exports:

| Export | Purpose |
|---|---|
| `SMALL_GRID_CUBE_SIZE` / `LARGE_GRID_CUBE_SIZE` / `ASTEROID_GRID_CUBE_SIZE` | Shared floor-grid sizing constants for small-grid, large-grid, and asteroid-scale viewer modes. |
| `initScene()` | Creates the scene, renderer, camera, controls, lighting, floor grid, pointer handlers, and resize observer. |
| `animate(time)` | Per-frame render loop that updates controls/free-fly movement, renders the scene, and refreshes render stats. |
| `replaceFloorGrid(bounds, gridSize, alignment = null)` | Rebuilds the scaled floor grid using SE small/large/asteroid grid cell semantics, optional per-axis lattice offsets, and updates fog density from the floor span. |
| `floorGridLayout(bounds, gridSize, alignment)` | Computes the snapped floor-grid cell layout used by both floor rendering and client-side voxel clipping. |
| `fitCameraToScene()` | Frames the active grid bounds and updates camera clipping planes/orbit target. |
| `updateSceneBounds(refit = false)` | Recomputes displayed relative-space bounds and refreshes the floor grid and sun marker placement. |
| `updateLighting()` | Applies the lighting toggle state to ambient fill, the directional sun, grid light group, and sun marker helpers. |
| `updateSunLightPosition()` | Positions the directional sun, target, marker, ray indicator, and fitted orthographic shadow camera in relative-grid space. |
| `disposeObjectTree(root)` | Disposes geometries and materials below a scene object. |
| `setCameraMode(mode)` | Switches between orbit and free-fly camera modes. |

## Dependencies
- `three` and `three/addons/controls/OrbitControls.js`.
- [`Quasar/wwwroot/viewer/state.js`](state.js.md) for global viewer runtime state and cached DOM elements.
- [`Quasar/wwwroot/viewer/geometry.js`](geometry.js.md) for bounds conversion.

## Notes
The hover path prioritizes visible overlay intersections before regular block/voxel readouts and ignores hidden mutually exclusive overlays and hidden overlay subgroups. Damage focus brightens damaged block masks on the hovered block's grid or modified voxel damage chunks from the hovered voxel body, and dims other visible damage masks.
