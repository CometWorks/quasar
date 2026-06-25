# Quasar/wwwroot/viewer/scene.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Three.js scene, camera, lighting, controls, animation, interaction, and render-stat runtime for the standalone grid viewer. It owns renderer setup, orbit/free-fly camera behavior, floor grid generation with per-axis block-boundary alignment, sun marker/lighting, object disposal, viewport resizing, hover readouts, and per-frame stats rendering.

## Structure

Key exports:

| Export | Purpose |
|---|---|
| `initScene()` | Creates the scene, renderer, camera, controls, lighting, floor grid, pointer handlers, and resize observer. |
| `animate(time)` | Per-frame render loop that updates controls/free-fly movement, renders the scene, and refreshes render stats. |
| `replaceFloorGrid(bounds, gridSize, alignment = null)` | Rebuilds the scaled floor grid using SE small/large grid cell semantics and optional per-axis lattice offsets. |
| `fitCameraToScene()` | Frames the active grid bounds and updates camera clipping planes/orbit target. |
| `updateSceneBounds(refit = false)` | Recomputes displayed bounds, floor grid, and sun marker placement. |
| `updateLighting()` | Applies sun toggle state to ambient/environment/directional lighting. |
| `updateSunLightPosition()` | Positions the sun light, target, marker, and ray indicator in relative-grid space. |
| `disposeObjectTree(root)` | Disposes geometries and materials below a scene object. |
| `setCameraMode(mode)` | Switches between orbit and free-fly camera modes. |

## Dependencies
- `three`, `three/addons/environments/RoomEnvironment.js`, and `three/addons/controls/OrbitControls.js`.
- [`Quasar/wwwroot/viewer/state.js`](state.js.md) for global viewer runtime state and cached DOM elements.
- [`Quasar/wwwroot/viewer/geometry.js`](geometry.js.md) for bounds conversion.

## Notes
The render stats panel is updated every frame and combines WebGL renderer counters with visibility/culling/object counts and values populated by `grid-renderer.js`.
