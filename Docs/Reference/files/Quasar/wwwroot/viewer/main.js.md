# Quasar/wwwroot/viewer/main.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Entry point for the standalone grid viewer page. It initializes DOM references, Three.js scene state, control wiring, persisted Content folder restoration, scene reloads, and high-level fetch timing for the stats panel.

## Structure

Lifecycle functions:

| Function | Purpose |
|---|---|
| `start()` | Runs on `DOMContentLoaded`, initializes the viewer, restores the Content folder, starts animation, and loads the first scene. |
| `reloadScene()` | Fetches a metadata-only entity scene from Quasar, records scene-fetch timing, and delegates rendering. |
| `selectContentFolder()` | Prompts for a local Content folder and re-renders the last scene when available. |

## Dependencies
- [`Quasar/wwwroot/viewer/state.js`](state.js.md), [`Quasar/wwwroot/viewer/scene.js`](scene.js.md), and [`Quasar/wwwroot/viewer/controls.js`](controls.js.md) for UI and renderer initialization.
- [`Quasar/wwwroot/viewer/quasar-api.js`](quasar-api.js.md) for scene HTTP fetches.
- [`Quasar/wwwroot/viewer/content-folder.js`](content-folder.js.md) for local asset folder selection/restoration.
- [`Quasar/wwwroot/viewer/grid-renderer.js`](grid-renderer.js.md) for scene rendering.
- [`Quasar/wwwroot/viewer/logging.js`](logging.js.md) for status/warning output.
