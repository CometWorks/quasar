# Grid Viewer Optimisation Plan

## Are The Batches Worth Keeping?

Yes. Keep the instanced model batches.

They solve a different problem from slow texture resolution:

- Batches reduce draw calls and live WebGL object count after the grid has loaded.
- Shared geometries avoid rebuilding the same model buffers for every block instance.
- Per-instance paint avoids allocating a full repeated per-vertex paint buffer for every rendered block.
- They do not meaningfully fix slow texture discovery, because texture discovery happens before or alongside material texture loads.

The batching change may make texture progress feel more visible because the grid can now remain responsive while texture loads continue. It is still worth keeping because it attacks steady-state rendering cost and RAM pressure. The remaining gap against `se-grid-render` is mostly in asset lookup and texture-loading pipeline design, not batching.

## Why Quasar Is Slower Than se-grid-render

The older `/home/space/Documents/se-grid-render` viewer uses a different asset pipeline.

`se-grid-render` gets stable `materialAssets` and `textureAssets` from its plugin/server. It can fetch raw asset bytes from `/v1/assets/{assetId}/raw`, so asset path resolution happens server-side once, then the browser loads texture bytes through HTTP. HTTP also gives the browser mature request scheduling, caching, and parallelism.

Quasar deliberately does not serve Space Engineers assets. The browser asks the user for a local Space Engineers `Content` folder and resolves logical paths through the File System Access API. That keeps Quasar's asset boundary tighter, but it makes the browser responsible for path discovery, case-insensitive lookup, file metadata checks, DDS parsing, and WebGL upload.

Current Quasar-specific costs:

- The viewer performs a blocking `resolveReferencedTextures(...)` pass before scene stats are complete.
- `resolveReferencedTextures(...)` resolves every listed/discovered texture path sequentially.
- `loadTexture(...)` resolves the same logical path again when the material actually loads it.
- `content-folder.js` has no path cache, no miss cache, and no per-directory lowercase entry cache.
- Case-insensitive fallback can enumerate directories repeatedly through `handle.entries()`.
- Texture decode/upload is capped to reduce RAM spikes, but that cap also increases total wall-clock time.
- DDS parsing and upload happen on the main thread.
- Texture load logging updates the whole log text repeatedly, which can add UI cost when many textures load.

## Goals

- Show the grid quickly with fallback materials.
- Load textures progressively without blocking scene construction.
- Avoid repeated File System Access API walks.
- Preserve Quasar's metadata-only server contract unless explicitly changed.
- Keep RAM bounded during DDS decode/upload.
- Keep batching because it improves steady-state draw calls and memory.

## Recommended Fixes

### 1. Instrument First

Add timing counters around:

- scene snapshot fetch
- model file resolution
- MWM parse time
- texture asset collection
- texture path resolution
- DDS file read time
- DDS parse time
- WebGL texture upload validation/init time
- log update cost if many lines are emitted

Expose these in the viewer stats or console so changes can be compared against `se-grid-render` with the same grid.

### 2. Remove Blocking Texture Pre-Resolution

Do not wait for every referenced texture path to resolve before completing scene construction.

Instead:

- Build geometry and materials immediately with generated/fallback textures.
- Queue only textures actually selected by visible/shared materials.
- Update texture stats progressively as each texture resolves, loads, fails, or is missing.
- Keep a lightweight listed-texture count from scene/model metadata for diagnostics.

This should make the viewer feel closer to `se-grid-render`: the grid appears first, then textures fill in.

### 3. Add Content Folder Lookup Caches

Add caches in `content-folder.js`:

- normalized logical path -> resolved file result
- normalized logical path -> null for misses
- directory path -> lowercase child-name map
- candidate path -> in-flight promise

Clear these caches when the user picks a different Content folder.

This avoids repeated directory traversal and repeated case-insensitive enumeration for common paths like `Textures/Models/...`.

### 4. Coalesce Texture Loads Before File Metadata

The current texture cache key depends on resolved file size and `lastModified`, so duplicate concurrent calls can still repeat path resolution before the cache key is known.

Add a logical-path in-flight cache keyed by:

```text
normalized logical path | slot/color-space role
```

Then, after resolution, keep the existing final cache key using file size and `lastModified` for invalidation.

### 5. Split Concurrency Controls

Use separate limits for different work types:

- Path/file resolution: higher concurrency, for example 16-32.
- File reads and DDS parse: medium concurrency, for example 4-8.
- WebGL upload/init validation: lower concurrency, for example 4-8 depending on measured RAM.

The current single low texture-load cap protects memory but also serializes too much work. Resolution is mostly async filesystem work and can safely be wider than DDS upload.

### 6. Reduce Texture Logging Cost

Avoid logging every successful DDS load to the DOM by default.

Options:

- log only failures and summary progress
- keep detailed successful texture logs in memory/console only
- batch DOM log updates with `requestAnimationFrame`

This matters when hundreds of textures load, because the current logger rebuilds `els.log.textContent` from all retained entries on each log call.

### 7. Add Browser Site Data For Metadata

Use IndexedDB for persistent metadata caches, not raw texture bytes initially.

Good cache candidates:

- selected Content folder handle, already implemented
- normalized logical path -> stored `FileSystemFileHandle` where supported
- logical path -> canonical casing/result metadata
- DDS header info keyed by path + size + `lastModified`
- parsed MWM material metadata keyed by model path + size + `lastModified`

Avoid caching raw DDS bytes by default because it duplicates the Space Engineers installation in browser storage and can consume a lot of site data. If later needed, make raw-byte caching explicit and bounded.

### 8. Consider Metadata-Only Material Assets

Quasar already sends `TextureAssets`, but the browser still discovers many material texture paths by parsing MWMs.

To narrow the gap without serving raw assets, extend the scene contract with metadata-only material descriptors similar to `se-grid-render`:

- material asset ID
- material name/subtype
- texture slot -> logical texture path
- usage hint

This keeps the server from sending raw asset bytes or mesh geometry, but gives the browser a better texture plan earlier.

Do not add a raw asset endpoint unless Quasar intentionally changes its current asset boundary.

## Implementation Order

1. Add timing instrumentation and summary stats.
2. Make texture loading progressive and remove blocking pre-resolution.
3. Add content-folder path, miss, directory, and in-flight caches.
4. Add logical texture load coalescing before file metadata is known.
5. Split resolution/read/upload concurrency limits.
6. Reduce successful texture-load DOM logging.
7. Add IndexedDB metadata caches.
8. Reassess whether metadata-only material descriptors are needed.

## Expected Outcome

After the first five steps, large textured grids should appear quickly and continue filling in textures without freezing the browser. RAM should stay bounded because DDS upload remains throttled, while path resolution should be much faster due to caching and wider async concurrency.

The batching work should keep steady-state render performance better than the previous per-block mesh approach, especially on grids with repeated armor and functional blocks.

## Implementation Notes

Implemented in the browser viewer:

- Timing counters now cover scene fetch, model path resolution, MWM reads/parsing, texture path resolution, DDS reads/parsing, and WebGL texture upload/init.
- Scene construction no longer blocks on pre-resolving every referenced texture path. Models render with fallback materials first, then selected material textures load progressively.
- `content-folder.js` caches resolved paths, misses, in-flight path lookups, and case-insensitive directory entry maps for the selected Content folder.
- Texture loading coalesces duplicate logical texture requests before file metadata is available, then keeps the existing file-size/last-modified cache key after resolution.
- Texture work now uses separate concurrency limits for path resolution, file reads/DDS parse work, and WebGL upload/init validation.
- Successful DDS loads are emitted to `console.debug`; the visible log is batched with `requestAnimationFrame` and remains focused on warnings/fallbacks.
