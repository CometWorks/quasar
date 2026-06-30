# Quasar/wwwroot/viewer/main.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Entry point for the standalone grid/asteroid viewer page. It initializes DOM references, parsed voxel and context URL support state, Three.js scene state, control wiring including log/statistics downloads, page-hide teardown, independent persisted Content and Mods folder restoration, scene reloads with a viewport loading/progress overlay, high-level fetch timing for the stats panel, and scene-loaded logging that can identify grid or voxel-only snapshots.

## Structure

Lifecycle functions:

| Function | Purpose |
|---|---|
| `start()` | Runs on `DOMContentLoaded`, initializes the viewer, restores Content and Mods folders independently, starts animation, and loads the first scene. |
| `reloadScene()` | Refreshes voxel/context URL support state, shows the loading overlay, fetches an entity scene from Quasar, records scene-fetch timing, delegates rendering with progress callbacks, and hides or updates the overlay. |
| `selectContentFolder()` | Prompts for a local Content folder and re-renders the last scene when available. |
| `selectModsFolder()` | Prompts for the global Mods folder and re-renders the last scene when available. |

The page lifecycle handlers dispose the viewer on `pagehide` so cached WebGL textures are released when navigating away, including via the browser back button. If the browser restores a disposed viewer from the back-forward cache, the page reloads to create a fresh WebGL context.

## Dependencies
- [`Quasar/wwwroot/viewer/state.js`](state.js.md), [`Quasar/wwwroot/viewer/scene.js`](scene.js.md), and [`Quasar/wwwroot/viewer/controls.js`](controls.js.md) for UI and renderer initialization.
- [`Quasar/wwwroot/viewer/quasar-api.js`](quasar-api.js.md) for scene HTTP fetches.
- [`Quasar/wwwroot/viewer/content-folder.js`](content-folder.js.md) for local asset folder selection/restoration.
- [`Quasar/wwwroot/viewer/grid-renderer.js`](grid-renderer.js.md) for scene rendering.
- [`Quasar/wwwroot/viewer/logging.js`](logging.js.md) for status/warning output and local text downloads.
