# Quasar/wwwroot/viewer/controls.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Wires the grid viewer control panel and keyboard input to scene actions. It connects reload, Content selection, Mods selection, render toggles including URL-gated voxel and voxel-deformation overlay visibility, mutually exclusive logistics and damaged-block overlays, context-mode reloads through the `context` query flag, the unified lighting toggle, camera mode switching, camera reset, and free-fly movement key tracking.

## Structure

| Export | Purpose |
|---|---|
| `configureContextControl()` | Enables/checks the `Context mode` checkbox according to parsed URL context state. |
| `configureVoxelControl()` | Enables/checks/disables the `Show voxels` checkbox according to parsed URL voxel support state. |
| `wireControls(actions)` | Attaches all DOM event listeners using callbacks supplied by `main.js`. |

Internal helpers identify free-fly keys (`WASD` and shift) and avoid capturing keyboard movement while the user is typing in form controls.

## Dependencies
- [`Quasar/wwwroot/viewer/state.js`](state.js.md) for cached elements and fly-mode state.
- [`Quasar/wwwroot/viewer/scene.js`](scene.js.md) for camera fitting, camera mode switching, lighting updates, and bounds updates.
