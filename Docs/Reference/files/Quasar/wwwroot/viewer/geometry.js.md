# Quasar/wwwroot/viewer/geometry.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Small Three.js geometry helper module used by the grid viewer. It converts scene DTO bounds to `THREE.Box3`, computes block-space bounding boxes from grid cell coordinates, and builds simple box meshes for proxy rendering.

## Structure

| Export | Purpose |
|---|---|
| `boundsToBox3(bounds)` | Converts DTO `{ min, max }` bounds into a `THREE.Box3`. |
| `blockBox(instance, gridSize)` | Computes the local bounding box for a block instance using SE grid-size semantics. |
| `createBoxMesh(box, material)` | Creates and centers a `THREE.Mesh` box for the supplied bounds. |

## Dependencies
- `three`.
- [`Quasar/wwwroot/viewer/math.js`](math.js.md) for vector DTO conversion.
