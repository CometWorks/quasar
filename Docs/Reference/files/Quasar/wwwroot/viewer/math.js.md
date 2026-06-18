# Quasar/wwwroot/viewer/math.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Numeric conversion utilities shared by the grid viewer. It converts Quasar matrix/vector DTOs into Three.js objects, provides deterministic hash colors, and normalizes numeric fallbacks.

## Structure

| Export | Purpose |
|---|---|
| `matrixDtoToThree(matrix)` | Converts a row/column DTO with `m11`...`m44` fields into a `THREE.Matrix4`. |
| `vec3(value)` | Converts `{ x, y, z }` into a `THREE.Vector3` with zero defaults. |
| `colorFromHash(text)` | Produces a stable HSL `THREE.Color` from text using an FNV-style hash. |
| `num(value, fallback)` | Parses a finite number or returns the supplied fallback. |

## Dependencies
- `three`.
