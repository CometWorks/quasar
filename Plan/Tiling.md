# Armor Texture Tiling Plan

## Findings

- The relevant Space Engineers path is render-side cube/armor material merging, not ship merge blocks.
- `MyCubeGrid.GetCubeParts(...)` computes `PatternOffset` as `(offsetU, offsetV, patternU, patternV)` from block grid position, face normal, topology, `PatternWidth`, `PatternHeight`, `ScaleTile*`, and model `PatternScale`.
- Quasar already captures this in `ViewerBlockModelPart.PatternOffset`, but the browser renderer currently ignores it.
- The game divides model UVs by MWM `PatternScale` before rendering:
  - `VRage.Render11/VRageRender/MyMeshes.cs`
  - `VRage.Render11/VRage/Render11/GeometryStage2/Model/MyMwmData.cs`
- The game then adds the pattern tile offset in the vertex shader:
  - `Content/Shaders/Geometry/VertexTemplateBase.hlsli`
  - `__texcoord0 += float2(offsetU / patternU, offsetV / patternV);`
- Current Quasar MWM parsing reads `TexCoords0` but ignores the `PatternScale` tag, so armor textures repeat too frequently.
- Current Quasar rendering ignores `modelParts[].patternOffset`, so armor atlas tile selection and continuity are missing.

## Implementation Plan

1. Add `PatternScale` parsing to `Quasar/wwwroot/viewer/mwm-loader.js`.
2. Add a `readFloatTag(offset)` helper beside `readStringTag(offset)`.
3. In `parseResolvedModelUncached`, read `PatternScale` if present, default to `1`.
4. Apply game-equivalent UV scaling when loading MWM texcoords: divide each UV component by `patternScale` after `TexCoords0` is read.
5. Include `patternScale` in the returned parsed model object for debugging and future use.
6. Preserve current `GeometryDataAsset` behavior. Since Space Engineers re-imports the geometry asset and reads its tags, the browser should use the geometry asset's own `PatternScale` when the stub redirects to geometry data.
7. Pass generated cube-part `patternOffset` through the renderer.
8. Change `createBlockMeshes` so `createModelMesh(...)` receives `part.patternOffset` for `block.modelParts`.
9. Pass `null` or omit pattern offset for `currentModelAssetId` and runtime `subparts`, since the game's cube-part patterning applies to generated cube topology parts.
10. Add a small UV transformation helper in `grid-renderer.js`.
11. Helper behavior:
    - If no UVs or no valid `patternOffset.z/w`, return original UVs.
    - `offsetU = patternOffset.x / patternOffset.z`.
    - `offsetV = patternOffset.y / patternOffset.w`.
    - Return a new `Float32Array` with `u + offsetU`, `v + offsetV`.
12. Use transformed UVs when setting the geometry `uv` attribute in `createModelMesh`.
13. Keep the existing texture wrapping behavior. Space Engineers relies on repeat addressing; the offset selects the correct pattern tile after UVs are scaled down.
14. Do not alter server DTOs or transmit any asset data. The needed metadata is already present.
15. Do not reimplement `MyCubeGrid.GetCubeParts` in JavaScript. The agent already uses the game method, so duplicating it client-side would add drift risk.
16. Update `Docs/GridViewer.md` to mention armor/cube-part pattern offsets and MWM `PatternScale`.
17. If generated reference docs include these viewer files, refresh only the relevant generated docs. If the handbook still excludes `wwwroot/viewer`, note that no generated reference update is needed.

## Verification Plan

1. Run `node --check Quasar/wwwroot/viewer/mwm-loader.js`.
2. Run `node --check Quasar/wwwroot/viewer/grid-renderer.js`.
3. Run `dotnet build Quasar/Quasar.csproj`.
4. Full solution build may still fail if local Space Engineers assemblies are not configured; treat that separately from this browser-only change.
5. Manual viewer check with vanilla armor:
   - Select local Space Engineers `Content`.
   - Load a grid with contiguous light/heavy armor.
   - Confirm armor textures tile less frequently than before.
   - Confirm adjacent armor pieces use varied/continuous pattern offsets instead of all sampling the same UV tile.
   - Confirm non-generated blocks and subparts are unchanged.
6. Regression check:
   - Missing models still render proxy boxes.
   - Paint masking still works with `AddMapsTexture`.
   - `*_alphamask.dds` is still not used as a paint mask.
