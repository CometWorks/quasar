# Quasar/wwwroot/viewer/texture-loader.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Browser-side texture resolver/loader for the grid viewer. It resolves logical texture paths against the selected local Content folder or a hinted mod source root, classifies color/data texture role from material slots or filename map tokens, disables Three.js' default vertical texture flip to match Space Engineers model UVs, preserves explicit DDS sRGB format tags for compressed uploads, caches extension-candidate hits and misses for the active asset-folder generation and root hint, coalesces duplicate logical texture loads before file metadata is known, caches completed textures by resolved root/path/size/mtime/color role, loads browser-native images, parses common SE DDS compressed formats, decodes textures to Canvas2D with an LCD sprite option that restores drawable alpha from RGB for premultiplied atlas masks, and throttles path resolution, file reads, and WebGL upload/init separately.

## Structure

| Export | Purpose |
|---|---|
| `resolveTextureAsset(asset)` | Resolves a texture asset DTO to a local file when possible. |
| `loadTexture(logicalPath, slot = "", options = {})` | Loads one logical texture for a material slot and optional root hint, using logical in-flight coalescing and final file metadata caching. |
| `resolveTextureFile(logicalPath, options = {})` | Tries and caches extension candidates (`.dds`, image extensions, raw path) through the root-aware asset resolver. |

Internal sections implement DDS header parsing for DXT1/DXT3/DXT5, BC4, BC5, DX10 BC7 variants, mip data slicing, `THREE.CompressedTexture` creation, no-flip color-space/wrap/anisotropy setup including sRGB-tagged compressed data maps, WebGL extension checks, upload preflight with `renderer.initTexture`, active-generation texture path candidate caches, and async queue throttles. Filename-only fallback classification recognizes map tokens/suffixes such as `_ng`, `_add`, `_alphamask`, and `_orm` without treating words like `Armor` in the path as ORM maps.

## Dependencies
- `three`.
- [`Quasar/wwwroot/viewer/content-folder.js`](content-folder.js.md) for local file resolution.
- [`Quasar/wwwroot/viewer/state.js`](state.js.md) for renderer access, texture caches, and in-flight logical loads.

## Notes
Missing logical textures throw errors marked with `isMissingLocalTexture` so `grid-renderer.js` can distinguish local misses from decode/upload failures in progressive stats. File metadata is requested through the resolved handle only after path resolution succeeds, but texture path, metadata, byte-read, parse, and upload timings are not recorded.
