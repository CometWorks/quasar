# Quasar/wwwroot/viewer/content-folder.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Browser local-asset helper for the grid viewer's Space Engineers `Content` and global `Mods` folders. It preserves Chromium File System Access persisted directory handles, falls back to Firefox-compatible folder file inputs, validates Content and Mods selections, tracks active scene mod roots, resolves logical asset paths through hinted mod roots before vanilla Content, supports direct `Mods/<mod-name>/...` paths, matches Workshop `123.sbm` scene names against `123` directories, and lazily reads unpacked mod directories or `.sbm` zip archives without sending asset bytes through Quasar.

## Structure

| Export | Purpose |
|---|---|
| `restoreContentFolder()` | Loads the persisted directory handle and reuses it when read permission is already granted. |
| `pickContentFolder()` | Opens `showDirectoryPicker` when available, otherwise uses the folder file input fallback, validates the selection, and clears asset caches. |
| `restoreModsFolder()` | Loads the persisted global Mods folder handle independently from Content. |
| `pickModsFolder()` | Prompts for the global Mods folder, accepts directory or `.sbm` top-level children, and clears asset caches. |
| `looksLikeContentFolder(handle)` | Checks for the top-level directories expected in an SE `Content` folder. |
| `looksLikeModsFolder(handle)` | Permissively checks for a directory containing mod directories or `.sbm` files. |
| `setSceneModRoots(mods)` | Rebuilds the active mod-root catalog from scene metadata. |
| `resolveAssetFile(logicalPath, options)` | Resolves root-aware assets from hinted mod roots, direct Mods paths, Content fallback, and bounded active-mod fallback. |
| `resolveContentFile(logicalPath)` | Compatibility wrapper for vanilla Content-oriented lookups. |
| `clearAssetFolderCaches()` / `clearContentFolderCaches()` | Clears resolved path, miss, in-flight lookup, lowercase directory-entry, metadata, texture, and archive caches. |
| `getAssetFolderCacheGeneration()` / `getContentFolderCacheGeneration()` | Returns the active asset-cache generation for dependent candidate caches. |

## Dependencies
- [`Quasar/wwwroot/viewer/state.js`](state.js.md) for selected folder state and texture cache reset.
- [`Quasar/wwwroot/viewer/logging.js`](logging.js.md) for selection status logging.
- Browser File System Access API, folder file input APIs, IndexedDB, and `@zip.js/zip.js` for local `.sbm` archive reads.

## Notes
Path resolution uses typed child lookups: intermediate segments call `getDirectoryHandle()` only, final segments call `getFileHandle()` only, and case-insensitive fallback enumerates a directory only after direct real-case and exact typed lookups miss. Firefox folder-input selections are wrapped in virtual directory/file handles with the same methods, so the existing lookup and cache behavior is shared. Directory nodes preserve canonical casing, are indexed by source root plus lowercase canonical path, cache lowercase child maps, coalesce in-flight child lookups, and remember negative file/directory misses so repeated bad candidates avoid filesystem calls.

Resolved path hits and misses are cached by generation, source root, source kind, mod name, and slash-normalized lowercase path. The returned `getFile()` function defers browser metadata snapshots until a loader needs size, mtime, or bytes, then caches the `File` by source root and canonical path with a separate metadata queue. `.sbm` archives are opened and indexed lazily for requested active mods, with archive readers reset when the selected folders change. The path lookup queue is widened to keep more browser File System Access operations in flight, while metadata snapshots use their own bounded queue so path resolution and file reads do not serialize behind a small shared limit. The stats panel receives cache diagnostics for path hits/misses, exact probes, directory enumeration, case fallback, negative-cache hits, and metadata-cache hits alongside timing metrics for metadata reads. In-memory caches are cleared when either selected asset folder changes.
