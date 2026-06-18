# Grid Viewer

Quasar includes a first-pass browser grid viewer for live grid entities listed on the Entities page.

## How to open it

1. Open **Entities**.
2. Select a connected server.
3. Refresh the entity list.
4. Click the eye icon beside a grid entity.

The viewer opens `/viewer/entity?agentId=...&entityId=...` and requests a scene snapshot from `/api/viewer/entities/{agentId}/{entityId}/scene`.

## Asset Boundary

Quasar does not serve Space Engineers assets to the browser.

The viewer endpoint returns metadata only:

- grid identity, transform, size, bounds, and static/dynamic state
- scene lighting metadata, including the current in-game sun direction
- loaded voxel body metadata and world bounds for planets, asteroids, moons, and voxel maps
- block definition IDs and block placement
- block cell coordinates, orientation, color mask, skin ID, build state, and integrity
- logical model paths for block definitions, current block models, generated cube parts, and runtime subparts
- logical texture paths only when they are available as metadata
- non-fatal warnings

The endpoint must not return:

- raw `.mwm` bytes
- raw texture bytes
- extracted vertices, indices, normals, UVs, or other mesh geometry
- a generic asset download API

## Local Content Folder

The browser asks the user to select their local Space Engineers `Content` folder. The folder should contain `Data`, `Models`, and `Textures` directories.

The viewer resolves logical model and texture paths case-insensitively where the browser file-system API permits it. The selected folder handle is stored in browser storage when supported, so the viewer can reuse it on later visits after permission is granted.

## Current Rendering Behavior

The browser always renders the selected grid in a relative view. The grid's in-game world transform is still used, but the view is re-centered on the grid and rotated into the grid's local frame so large world coordinates do not reduce browser-side precision. Voxel body proxies and the sun direction are transformed through the same relative frame.

The floor grid is scaled to the displayed grid bounds, padded by two major squares, and uses Space Engineers cube-size semantics: 0.5 m sub-squares are shown when the extent is reasonable, while major lines align to the active grid size (`0.5 m` for small grids and `2.5 m` for large grids). Very large extents fall back to coarser major-line spacing to avoid excessive browser geometry.

The viewer parses locally resolved `.mwm` files in the browser and renders mesh geometry for block models, generated cube-part models, and runtime subpart models. Current parsing covers the render mesh tags needed for static geometry (`Vertices`, `Normals`, `TexCoords0`, `MeshParts`, `PatternScale`) and follows `GeometryDataAsset` indirection used by stub MWMs.

Armor and other generated cube-part models use the game-provided `PatternOffset` metadata from `MyCubeGrid.GetCubeParts(...)`. The browser applies the MWM `PatternScale` to model UVs and then applies the cube-part pattern offset before sampling wrapped material textures, matching Space Engineers' armor texture tiling and adjacent-block atlas variation more closely. Regular block models and runtime subparts do not receive cube-part pattern offsets.

Model material textures are also resolved from the selected local `Content` folder. Browser-native image files are loaded directly, and DDS material textures are parsed in the browser for common Space Engineers compressed formats including DXT1/DXT3/DXT5, BC4, BC5, and DX10 BC7. DDS upload still depends on the user's browser/GPU exposing the matching WebGL compressed-texture extension, such as `WEBGL_compressed_texture_s3tc`, `EXT_texture_compression_rgtc`, or `EXT_texture_compression_bptc`.

Applied block paint is rendered client-side from the scene `colourMaskHsv` metadata. For textured models, the viewer uses Space Engineers-style color masking: base color comes from `ColorMetalTexture`/diffuse textures, paint strength comes from `AddMapsTexture`/extension-map alpha, and `*_alphamask.dds` is treated only as alpha/cutout data rather than a paint mask. When a local model cannot be parsed, proxy boxes use the same block paint color as a visual fallback.

The viewer statistics panel reports unique model assets listed by the scene, model files found locally, parsed models, missing models, and the equivalent texture counts discovered from scene texture metadata and locally parsed MWM material groups. Texture counts are unique by logical texture path; `loaded` counts textures the browser successfully decoded/uploaded, while `failed` covers locally found textures that could not be used, such as unsupported DDS/WebGL compression combinations.

Loaded voxel bodies are shown as metadata-only proxies: planets use a wire sphere from the in-game radius metadata, and other voxel maps use their world bounds. The `Show voxels` toggle controls these proxies. Quasar still does not transmit voxel storage samples or generated voxel mesh geometry.

The `Show sun` toggle controls the directional sun light and marker. When enabled, the sun position comes from `MySector.DirectionToSunNormalized` in the running game and is transformed into the grid-relative view; the visible sun ray points back toward the grid along the same direction used by the directional light. The sun-on ambient fill is intentionally low and uniform. When disabled, the viewer uses slightly stronger uniform ambient and environment lighting so the grid remains readable without directional sun.

The stats panel also includes live WebGL and viewport counters such as draw calls, triangles, lines, points, GPU geometries/textures, shader programs, renderables, visible objects, culled objects, meshes, sprites, and lights.

Missing or unparseable local models and missing or unloadable textures are non-fatal. The viewer logs warnings and keeps the scene visible with proxy boxes and generated fallback materials where needed.

## Mods

Modded grids require matching local mod content. If the selected local `Content` folder does not include the referenced mod assets, affected blocks fall back to proxy rendering and warnings are shown.

## Server-Side Notes

The scene snapshot is captured by `Quasar.Agent` on the game thread through the existing agent command/result WebSocket flow. The command is `GetEntityRenderScene`, and the shared DTOs live in `Magnetar.Protocol`.

The dedicated-server agent deliberately does not reference client render assemblies to resolve skin texture-change payloads. It sends `SkinSubtypeId` and other block metadata; local browser-side content handling is responsible for visual fallbacks and any future local skin-definition resolution.
