# Quasar/wwwroot/viewer/state.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Shared mutable runtime state and DOM-element cache for the standalone grid viewer ES modules. It centralizes Three.js objects including the directional sun, its target, optional sun marker helpers, captured grid light groups, logistics and damaged-block overlay groups, and voxel group, renderer animation/teardown state, camera/fly-mode state, current scene/bounds, voxel and context URL support state, logical context bounds, context clip bounds, context grid IDs, floor-grid alignment metadata, loading overlay DOM elements, local Content and global Mods folder handles or folder-input adapters, active scene mod roots, model/texture caches and cache generation, progressive texture stats, timing counters, and displayed stats. Grid-size defaults are assigned by renderer/scene modules so this state module does not import `scene.js` and avoids a circular ES-module dependency.

## Structure

| Export | Purpose |
|---|---|
| `state` | Singleton object holding renderer/scene/camera references, animation and disposal flags, groups including logistics and damaged-block overlays, controls, loaded-scene metadata including voxel/context support, logical context bounds, context clip bounds, floor-grid alignment, selected asset folders, active mod roots, cache maps and generations, texture stats, timing counters, and UI stats. |
| `els` | Object populated with commonly used DOM elements from `index.html`. |
| `cacheElements()` | Fills `els` by looking up all required viewer element IDs. |

## Dependencies
- Browser DOM APIs.

## Notes
This file intentionally keeps cross-module viewer state explicit rather than introducing a framework store for the static viewer page.
