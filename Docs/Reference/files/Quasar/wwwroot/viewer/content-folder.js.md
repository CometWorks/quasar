# Quasar/wwwroot/viewer/content-folder.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Browser local-content helper for the grid viewer's Space Engineers `Content` folder. It preserves the Chromium File System Access path with persisted directory handles, falls back to a Firefox-compatible folder file input, validates the expected `Data`, `Models`, and `Textures` directories, resolves logical asset and SBC data paths through a lazy case-insensitive path cache, and separates path lookup from deferred `getFile()` snapshots.

## Structure

| Export | Purpose |
|---|---|
| `restoreContentFolder()` | Loads the persisted directory handle and reuses it when read permission is already granted. |
| `pickContentFolder()` | Opens `showDirectoryPicker` when available, otherwise uses the folder file input fallback, validates the selection, and clears asset caches. |
| `looksLikeContentFolder(handle)` | Checks for the top-level directories expected in an SE `Content` folder. |
| `resolveContentFile(logicalPath)` | Normalizes a logical asset path, tries known extension candidates, and returns `{ logicalPath, canonicalPath, fileHandle, getFile }` or `null`. |
| `clearContentFolderCaches()` | Clears resolved path, miss, in-flight lookup, and lowercase directory-entry caches. |
| `getContentFolderCacheGeneration()` | Returns the active Content-cache generation for dependent candidate caches. |

## Dependencies
- [`Quasar/wwwroot/viewer/state.js`](state.js.md) for selected folder state and texture cache reset.
- [`Quasar/wwwroot/viewer/logging.js`](logging.js.md) for selection status logging.
- Browser File System Access API, folder file input APIs, and IndexedDB.

## Notes
Path resolution uses typed child lookups: intermediate segments call `getDirectoryHandle()` only, final segments call `getFileHandle()` only, and case-insensitive fallback enumerates a directory only after direct real-case and exact typed lookups miss. Firefox folder-input selections are wrapped in virtual directory/file handles with the same methods, so the existing lookup and cache behavior is shared. Directory nodes preserve canonical casing, are indexed by lowercase canonical path, cache lowercase child maps, coalesce in-flight child lookups, and remember negative file/directory misses so repeated bad candidates avoid filesystem calls.

Resolved path hits and misses are cached by slash-normalized lowercase path. The returned `getFile()` function defers browser metadata snapshots until a loader needs size, mtime, or bytes, then caches the `File` by canonical path with a separate metadata queue. The path lookup queue is widened to keep more browser File System Access operations in flight, while metadata snapshots use their own bounded queue so path resolution and file reads do not serialize behind a small shared limit. The stats panel receives cache diagnostics for path hits/misses, exact probes, directory enumeration, case fallback, negative-cache hits, and metadata-cache hits alongside timing metrics for metadata reads. In-memory caches are cleared when the active Content folder changes.
