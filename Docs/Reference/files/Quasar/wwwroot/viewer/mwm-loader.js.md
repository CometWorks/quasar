# Quasar/wwwroot/viewer/mwm-loader.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Browser-side parser for locally selected Space Engineers `.mwm` render model files. It resolves model paths through the root-aware asset helper using optional mod root hints, fetches lazy file metadata only when cache keys or bytes are needed, caches parsed models by root/path/size/mtime, follows `GeometryDataAsset` indirection through the same source root first, and extracts static mesh geometry/material data plus armor skinning metadata (`BoneMapping`, `BlendIndices`, `BlendWeights`) and `GLASS` transparent-material names without recording model timing counters.

## Structure

| Export | Purpose |
|---|---|
| `resolveModelAsset(asset)` | Resolves and parses one model asset DTO, returning `parsed`, `proxy`, or `missing` status plus diagnostics. |

Internal structure:
- `parseResolvedModel()` and `parseResolvedModelUncached()` implement cache lookup, recursion protection, lazy metadata reads, file reads, tag parsing, geometry-asset redirects, and render group construction.
- `MwmReader` is a binary reader for the subset of MWM tags needed by the viewer: header/index, strings, vertices, normals, texcoords, mesh parts, skinning vectors, material texture slots, `GLASS` material names, and selected scalar values.
- Helpers unpack half-floats/normals, reverse MWM/Direct3D triangle winding for Three.js/WebGL front faces, scale raw UVs by `PatternScale` before renderer-side cube-part pattern offsets, and order mesh techniques so material groups are stable.

## Dependencies
- [`Quasar/wwwroot/viewer/content-folder.js`](content-folder.js.md) for local asset resolution.

## Notes
The parser intentionally extracts render metadata from local user files in the browser; Quasar still does not serve raw MWM bytes or extracted geometry. Parsed model results preserve `rootId`/`rootKind` so material textures discovered inside the MWM can resolve from the same mod root before Content fallback. `GeometryDataAsset` redirects use the same path resolver and do not add separate timing stats.
