# Grid Viewer Slow Path Findings

Source log: `/tmp/quasar-viewer.log`

## Summary

The slow path is browser-side logical path resolution against the selected local Space Engineers `Content` folder. The log does not point to raw MWM file reads or MWM parsing as the main source of the >100 ms timings.

The primary cause is case-insensitive fallback lookup through the browser File System Access API. The viewer asks for paths using `Large` / `Small` directory casing, while the local Content folder contains lowercase `large` paths. Exact lookup fails quickly, then the resolver enumerates directories to find a case-insensitive match. That enumeration is expensive on large Content directories.

## Evidence

Bad-cased default model paths are slow on cold resolve:

- `Models/Cubes/Large/Armor/LargeBlockArmorBlock.mwm`: `445.9 ms`
- `Models/Cubes/Large/Armor/LargeBlockArmorSlope.mwm`: `403.7 ms`
- `Models/Cubes/Large/CubeBlock/LargeBlockArmorBlock.mwm`: `357.5 ms`
- `Models/Cubes/Large/Cockpit/LargeBlockCockpit.mwm`: `353.7 ms`
- `Models/Cubes/Large/Reactor/LargeBlockSmallGenerator.mwm`: `358.8 ms`
- `Models/Cubes/Large/GravityGenerator/GravityGenerator.mwm`: `365.7 ms`
- `Models/Cubes/Small/Armor/SmallBlockArmorBlock.mwm`: `319.9 ms`
- `Models/Cubes/Small/Cockpit/SmallBlockCockpit.mwm`: `162.3 ms`

The exact probes for those paths fail quickly, which shows the requested path casing does not match the on-disk path:

- failures are around `0.5-2.3 ms`
- error: `A requested file or directory could not be found at the time an operation was processed.`

The fallback discovery pass finds lowercase paths:

- `Models/Cubes/large/10YearStatue_Construction_1_LOD0.mwm`
- `Models/Cubes/large/10YearStatue_Construction_1_LOD1.mwm`
- and other `Models/Cubes/large/...` files

Those exact lowercase paths are fast:

- exact file probes: `0.7-4.8 ms`
- cold resolves: `1.0-1.4 ms`
- warm resolves: `0-0.1 ms`

Directory enumeration is the expensive fallback step:

- `discover-directory:Models/Cubes/large`: `354.6 ms`

Raw MWM reads are fast enough not to explain the issue:

- repeated `arrayBuffer()` reads are mostly `0.4-6.8 ms`

MWM parsing is also fast enough not to explain the issue:

- cold parses are mostly `2.7-10.8 ms`
- warm parses are `0-0.1 ms`

The tiny synthetic ship confirms the same pattern:

- `modelResolutionTotal`: `112.3 ms`
- `mwmParse max`: `4 ms`
- `mwmFileRead max`: `45.7 ms`
- `contentFileResolve max`: `355.4 ms`
- `texturePathResolve max`: `356.7 ms`
- `contentFileMetadataRead max`: `178.9 ms`

## Root Cause

The viewer's logical asset paths are not normalized to the local Space Engineers Content folder casing before using exact File System Access API probes.

When a path segment has the wrong case, the resolver does this:

1. Exact directory probe fails.
2. Exact file probe fails where applicable.
3. Directory entries are enumerated to build a lowercase lookup map.
4. The matching handle is found from that map.

Step 3 is expensive for large Content directories and creates the observed >100 ms timings.

## Secondary Impact

Texture path resolution shows the same behavior:

- `texturePathResolve max`: `356.7 ms`
- `textureFileResolution max`: `355.4 ms`

This means fixing only model paths may improve MWM loading but still leave visible texture-resolution stalls if texture path casing has the same mismatch.

## Recommended Fix

Normalize known Space Engineers Content path segments before probing the File System Access API.

High-value candidates:

- `Models/Cubes/Large` -> `Models/Cubes/large`
- `Models/Cubes/Small` -> `Models/Cubes/small`
- equivalent common texture directory casing mismatches after checking sampled texture paths

The resolver should try the normalized path first so common vanilla asset lookups hit exact handles and avoid directory enumeration. Keep the existing case-insensitive fallback for modded assets and unexpected local layouts.

## Expected Result

For common vanilla grid assets, cold logical model resolves should move from `~160-446 ms` to roughly the exact-path range observed in the diagnostics run:

- exact file probe: `0.7-4.8 ms`
- cold resolve: `1.0-1.4 ms`

That should remove the major slow path from model loading. Texture path normalization should be investigated next because the tiny-ship run shows texture resolution can hit the same `~350 ms` fallback cost.
