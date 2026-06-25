# Quasar/wwwroot/viewer/state.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Shared mutable runtime state and DOM-element cache for the standalone grid viewer ES modules. It centralizes Three.js objects, camera/fly-mode state, current scene/bounds, floor-grid alignment metadata, local Content folder handles or folder-input adapters, model/texture caches, progressive texture stats, timing counters, and displayed stats.

## Structure

| Export | Purpose |
|---|---|
| `state` | Singleton object holding renderer/scene/camera references, groups, controls, loaded-scene metadata including floor-grid alignment, cache maps, texture stats, timing counters, and UI stats. |
| `els` | Object populated with commonly used DOM elements from `index.html`. |
| `cacheElements()` | Fills `els` by looking up all required viewer element IDs. |

## Dependencies
- Browser DOM APIs.

## Notes
This file intentionally keeps cross-module viewer state explicit rather than introducing a framework store for the static viewer page.
