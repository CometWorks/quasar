# Browser Content Path Cache Plan

## Context

Grid viewer model loading is currently dominated by browser filesystem lookup overhead, not model size or transfer throughput. Logs show simple path-resolution, handle-probe, and metadata calls routinely taking 120-490 ms. Tiny `.mwm` files can take hundreds of milliseconds to read, while larger files sometimes read faster, which points to fixed per-operation latency and resolver behavior rather than raw disk throughput.

The `se-linux-compat` project contains a C# `PathCache` with a useful two-level design: a flat immutable cache for known static roots, plus per-directory dynamic casing caches elsewhere. The browser viewer needs a similar idea adapted to File System Access handles.

## Goals

- Resolve mixed-case Space Engineers asset paths with fewer filesystem calls.
- Cache actual casing at each directory level.
- Stop probing files as directories.
- Cache successful and failed path resolutions.
- Avoid `getFile()` metadata reads until metadata or bytes are actually needed.
- Keep diagnostics clear: path resolution, metadata reads, and byte reads should be separate timings.

## Phase 1: Typed Child Lookup

Current resolver behavior uses generic child lookup for both directory and file positions. That causes useless calls such as `getDirectoryHandle("SquarePlateConstruction_1.mwm")`, which fails with `TypeMismatchError` and can take hundreds of milliseconds.

Replace generic `getChild(handle, name)` usage with typed methods:

- `getDirectoryChild(parent, segment)` for intermediate path segments.
- `getFileChild(parent, segment)` for final file segments.

Rules:

- Intermediate path segments only call `getDirectoryHandle()`.
- Final file segments only call `getFileHandle()`.
- Known file extensions include `.mwm`, `.dds`, `.png`, `.jpg`, `.jpeg`, and `.webp`.
- Case-insensitive fallback should enumerate the parent only after the exact typed lookup misses.

Expected impact:

- Removes wrong-kind filesystem calls for filenames.
- Eliminates slow `TypeMismatchError` directory probes for `.mwm` files.
- Reduces each model path resolution by at least one filesystem operation on common paths.

## Phase 2: Content Path Cache Object

Create a resolver object inside `content-folder.js` or a new `content-path-cache.js` module.

Suggested shape:

```js
ContentPathCache {
  rootHandle
  generation
  resolvedFilesByLowerPath: Map<string, ResolvedFile | null>
  inFlightResolutions: Map<string, Promise<ResolvedFile | null>>
  directoryNodesByLowerPath: Map<string, DirectoryNode>
}

DirectoryNode {
  canonicalPath
  handle
  childrenByLowerName: Map<string, ChildEntry>
  exactMisses: Set<string>
  fileMisses: Set<string>
  directoryMisses: Set<string>
  enumerationPromise
}

ChildEntry {
  name
  lowerName
  kind // "file" | "directory"
  handle
  canonicalPath
}
```

Important details:

- Cache keys use slash-normalized lowercase paths.
- Values preserve actual on-disk casing.
- Directory nodes cache each level's actual child casing.
- Cache misses as well as hits, so repeated bad candidates do not hit disk repeatedly.
- Clear the whole cache when the selected Content folder changes.

## Phase 3: Lazy Case-Correct Segment Resolution

Adapt the `se-linux-compat` segment-walk idea for browser handles.

Resolution flow:

1. Normalize input:
   - trim whitespace
   - convert `\` to `/`
   - remove leading `./`
   - remove leading `Content/`
   - collapse duplicate slashes
2. Split into path segments.
3. Walk from the selected Content root:
   - for each directory segment, use an existing `DirectoryNode` when possible
   - try exact `getDirectoryHandle()` first
   - if exact lookup misses, enumerate the parent once and use lowercase lookup
4. For the final file segment:
   - try exact `getFileHandle()` first
   - if exact lookup misses, enumerate the parent once and use lowercase lookup
5. Return canonical logical path plus the file handle.

This mirrors `PathCache.WalkFromRoot(...)` in `se-linux-compat`, except browser cache values are File System Access handles instead of absolute filesystem paths.

## Phase 4: Avoid Full Recursive Startup Index

Do not build a complete recursive Content index at folder selection time. Browser recursive enumeration can be expensive and would delay first render.

Use lazy enumeration first:

- Enumerate a directory only after an exact lookup misses.
- Cache the lowercase child map after enumeration.
- Hot paths such as `Models/Cubes/Large/Armor` become cheap after the first miss.

Optional later optimization:

- Add targeted background preindexing for hot roots:
  - `Models/Cubes`
  - `Textures`
- Run preindexing only after proxies are visible.
- Use low concurrency.
- Abort and restart when the selected Content folder changes.

## Phase 5: Defer Metadata Reads

`resolveContentFile()` currently returns a `File`, which forces `getFile()` during resolution. That mixes path lookup latency with metadata snapshot latency.

Change the result shape to defer metadata:

```js
{
  logicalPath,
  canonicalPath,
  fileHandle,
  getFile: async () => File
}
```

Then:

- Model and texture loaders call `getFile()` only when they need size, mtime, or bytes.
- Path resolution logs no longer include `getFile()` latency.
- Cache metadata separately by canonical path:
  - `fileMetadataByCanonicalPath`
  - `inFlightMetadataByCanonicalPath`

Expected logs after this change:

- `resolve Content file path`: handle/path resolution only.
- `read local file metadata`: `getFile()` snapshot only.
- `read MWM model file` / `read DDS texture file`: `arrayBuffer()` only.

## Phase 6: Candidate Extension Cache

Extensionless logical paths currently test multiple candidate filenames repeatedly.

Model candidates:

- `path.mwm`
- `path`

Texture candidates:

- `path.dds`
- `path.png`
- `path.jpg`
- `path.jpeg`
- `path.webp`
- `path`

Plan:

- Cache every candidate result, including misses.
- Once a candidate resolves, cache the extensionless key to the same canonical result.
- Cache `_LOD0` geometry paths aggressively because descriptor MWMs commonly redirect to them.

Example:

```text
models/cubes/large/armor/slope2tipconstruction_1_lod0 -> Models/Cubes/large/armor/Slope2TipConstruction_1_LOD0.mwm
```

## Phase 7: Avoid Nested Timing Double-Counting

`resolve MWM GeometryDataAsset file` currently wraps `resolveContentFile()`, so it mirrors the inner `resolve Content file path` timing. Summing both double-counts elapsed time.

Preferred logging change:

- Keep `resolve Content file path` as the actual filesystem/path-cache timing.
- Change `resolve MWM GeometryDataAsset file` to a lightweight non-filesystem diagnostic, or mark it clearly as inclusive.
- Add request IDs only if operation correlation remains unclear.

## Phase 8: Filesystem Lookup Concurrency Control

Even after caching, the browser File System Access API may serialize or slow down under heavy concurrent operations.

Add a dedicated lookup queue around actual filesystem handle calls:

- exact file handle probes
- exact directory handle probes
- directory enumeration
- `getFile()` metadata reads

Start with conservative limits:

- lookup concurrency: `4` or `6`
- metadata concurrency: `4`
- byte-read concurrency remains separate from lookup concurrency

Validation:

- If per-call latency drops after reducing lookup concurrency, the browser or filesystem layer was saturated.
- If latency stays high, the cache should still reduce total operations enough to improve overall load time.

## Phase 9: Cache Diagnostics

Add counters that prove the cache is working:

- `pathCacheHit`
- `pathCacheMiss`
- `directoryCacheHit`
- `directoryEnumeration`
- `exactFileProbe`
- `exactDirectoryProbe`
- `caseFallbackHit`
- `negativeCacheHit`
- `metadataCacheHit`
- `filesystemCallsAvoided` if easy to track

Expected log changes:

- Far fewer exact probes after warm-up.
- No directory probes for `.mwm` filenames.
- Geometry `_LOD0` paths become cache hits after first resolution.
- `resolve Content file path` timings drop sharply after hot directories are cached.
- Actual `read MWM model file` timings may remain slow if browser `arrayBuffer()` itself is slow, but path resolution should no longer dominate.

## Phase 10: Documentation

Update:

- `Docs/GridViewer.md`
- `Docs/Reference/files/Quasar/wwwroot/viewer/content-folder.js.md`
- `Docs/Reference/files/Quasar/wwwroot/viewer/mwm-loader.js.md`
- `Docs/Reference/files/Quasar/wwwroot/viewer/texture-loader.js.md`
- `Docs/Reference/files/Quasar/wwwroot/viewer/logging.js.md`
- `Docs/Reference/Modules/Quasar.Host.md`
- `Docs/Reference/Index.md`

Document:

- Lazy case-insensitive Content path cache.
- Typed file/directory lookups.
- Positive and negative resolution caching.
- Metadata deferral.
- Difference between path resolution, metadata read, and byte-read timings.

## Recommended First Implementation Slice

Start with the smallest high-value change:

1. Split `getChildDirectory()` and `getChildFile()` so they no longer call generic `getChild()`.
2. Make each typed function use the per-directory lowercase map cache when exact lookup misses.
3. Keep the existing `resolvedPathCache` and in-flight resolution cache, but ensure misses are cached too.
4. Stop wrapping `GeometryDataAsset` resolution with an additional filesystem timing, or mark it as inclusive.
5. Run the existing viewer logging and compare before/after timings.

Then implement the full lazy `ContentPathCache` object if the first slice does not reduce resolution time enough.
