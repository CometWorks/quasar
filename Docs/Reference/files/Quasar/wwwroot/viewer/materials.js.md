# Quasar/wwwroot/viewer/materials.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Shared material helper module for simple grid-viewer proxy and wireframe rendering. It creates cached deterministic block materials and line materials, and exposes a cache disposal hook.

## Structure

| Export | Purpose |
|---|---|
| `blockMaterial(key, opacity = 0.72)` | Returns a cached `MeshStandardMaterial` with a deterministic color derived from `key`. |
| `wireMaterial(color = 0x6ee7f9)` | Returns a cached transparent `LineBasicMaterial`. |
| `disposeMaterialCache()` | Disposes all cached materials and clears the cache. |

## Dependencies
- `three`.
- [`Quasar/wwwroot/viewer/math.js`](math.js.md) for deterministic color generation.
