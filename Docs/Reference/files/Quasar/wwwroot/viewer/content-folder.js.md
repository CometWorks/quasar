# Quasar/wwwroot/viewer/content-folder.js

**Module:** Quasar.Host  **Kind:** JS  **Tier:** 3

## Summary
Browser File System Access helper for the grid viewer's local Space Engineers `Content` folder. It restores or stores the selected folder handle in IndexedDB, validates the expected `Data`, `Models`, and `Textures` directories, resolves logical asset paths case-insensitively, and keeps in-memory path/directory caches for the active folder.

## Structure

| Export | Purpose |
|---|---|
| `restoreContentFolder()` | Loads the persisted directory handle and reuses it when read permission is already granted. |
| `pickContentFolder()` | Opens `showDirectoryPicker`, validates the selection, stores the handle, and clears asset caches. |
| `looksLikeContentFolder(handle)` | Checks for the top-level directories expected in an SE `Content` folder. |
| `resolveContentFile(logicalPath)` | Normalizes a logical asset path, tries known extension candidates, and returns `{ logicalPath, file }` or `null`. |
| `clearContentFolderCaches()` | Clears resolved path, miss, in-flight lookup, and lowercase directory-entry caches. |

## Dependencies
- [`Quasar/wwwroot/viewer/state.js`](state.js.md) for selected folder state and texture cache reset.
- [`Quasar/wwwroot/viewer/logging.js`](logging.js.md) for selection status logging.
- Browser File System Access API and IndexedDB.

## Notes
Case-insensitive fallback enumerates directory entries only once per `FileSystemDirectoryHandle` and caches the lowercase map in a `WeakMap`. Caches are intentionally in-memory and are cleared when the active Content folder changes.
