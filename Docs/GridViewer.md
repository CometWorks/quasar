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

The viewer parses locally resolved `.mwm` files in the browser and renders mesh geometry for block models, generated cube-part models, and runtime subpart models. Current parsing covers the render mesh tags needed for static geometry (`Vertices`, `Normals`, `TexCoords0`, `MeshParts`, `PatternScale`) and follows `GeometryDataAsset` indirection used by stub MWMs.

Armor and other generated cube-part models use the game-provided `PatternOffset` metadata from `MyCubeGrid.GetCubeParts(...)`. The browser applies the MWM `PatternScale` to model UVs and then applies the cube-part pattern offset before sampling wrapped material textures, matching Space Engineers' armor texture tiling and adjacent-block atlas variation more closely. Regular block models and runtime subparts do not receive cube-part pattern offsets.

Model material textures are also resolved from the selected local `Content` folder. Browser-native image files are loaded directly, and DDS material textures are parsed in the browser for common Space Engineers compressed formats including DXT1/DXT3/DXT5, BC4, BC5, and DX10 BC7. DDS upload still depends on the user's browser/GPU exposing the matching WebGL compressed-texture extension, such as `WEBGL_compressed_texture_s3tc`, `EXT_texture_compression_rgtc`, or `EXT_texture_compression_bptc`.

Applied block paint is rendered client-side from the scene `colourMaskHsv` metadata. For textured models, the viewer uses Space Engineers-style color masking: base color comes from `ColorMetalTexture`/diffuse textures, paint strength comes from `AddMapsTexture`/extension-map alpha, and `*_alphamask.dds` is treated only as alpha/cutout data rather than a paint mask. When a local model cannot be parsed, proxy boxes use the same block paint color as a visual fallback.

Missing or unparseable local models and missing textures are non-fatal. The viewer logs warnings and keeps the scene visible with proxy boxes and generated fallback materials where needed.

## Mods

Modded grids require matching local mod content. If the selected local `Content` folder does not include the referenced mod assets, affected blocks fall back to proxy rendering and warnings are shown.

## Server-Side Notes

The scene snapshot is captured by `Quasar.Agent` on the game thread through the existing agent command/result WebSocket flow. The command is `GetEntityRenderScene`, and the shared DTOs live in `Magnetar.Protocol`.

The dedicated-server agent deliberately does not reference client render assemblies to resolve skin texture-change payloads. It sends `SkinSubtypeId` and other block metadata; local browser-side content handling is responsible for visual fallbacks and any future local skin-definition resolution.
