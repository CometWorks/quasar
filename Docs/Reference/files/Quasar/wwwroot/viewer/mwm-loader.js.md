# Quasar/wwwroot/viewer/mwm-loader.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Browser-side parser for locally selected Space Engineers `.mwm` render model files. It resolves model paths through the Content folder helper, caches parsed models by path/size/mtime, follows `GeometryDataAsset` indirection, extracts static mesh geometry/material data, and records file-read/parse timing counters.

## Structure

| Export | Purpose |
|---|---|
| `resolveModelAsset(asset)` | Resolves and parses one model asset DTO, returning `parsed`, `proxy`, or `missing` status plus diagnostics. |

Internal structure:
- `parseResolvedModel()` and `parseResolvedModelUncached()` implement cache lookup, recursion protection, file reads, tag parsing, geometry-asset redirects, and render group construction.
- `MwmReader` is a binary reader for the subset of MWM tags needed by the viewer: header/index, strings, vertices, normals, texcoords, mesh parts, materials, and selected scalar values.
- Helpers unpack half-floats/normals, scale UVs by `PatternScale`, and order mesh techniques so material groups are stable.

## Dependencies
- [`Quasar/wwwroot/viewer/content-folder.js`](content-folder.js.md) for local asset resolution.
- [`Quasar/wwwroot/viewer/state.js`](state.js.md) for timing counters.

## Notes
The parser intentionally extracts render metadata from local user files in the browser; Quasar still does not serve raw MWM bytes or extracted geometry.
