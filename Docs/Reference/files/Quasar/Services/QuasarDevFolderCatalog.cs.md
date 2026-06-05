# Quasar/Services/QuasarDevFolderCatalog.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Manages a persisted list of local developer plugin folders used during plugin development. Each entry (`QuasarDevFolderSelection`) maps a folder path and plugin manifest data file to an optional plugin-id override and debug-build flag. The catalog loads from and saves to a single `dev-folders.json` file in the Quasar data directory, and fires a `Changed` event after every mutation.

## Structure
**Namespace:** `Quasar.Services`

**Type:** `QuasarDevFolderCatalog` (sealed class)

| Member | Description |
|---|---|
| `event Action? Changed` | Fired after any upsert or delete. |
| `GetDevFolders()` | Returns defensive clones of all entries (unsorted beyond normalization order). |
| `GetDevFolder(folderPath, dataFile)` | Looks up by case-insensitive (folderPath, dataFile) pair. |
| `UpsertAsync(devFolder, ct)` | Normalizes, inserts or replaces, saves atomically. |
| `DeleteAsync(folderPath, dataFile, ct)` | Removes matching entries, saves atomically. |

Private helpers:
- `Load()` — reads `dev-folders.json`; returns empty list if file absent or invalid
- `SaveAsync(ct)` — atomic write via `AtomicFileWriter`
- `NormalizeList()` — filters blanks, deduplicates by (FolderPath, DataFile), sorts by Name
- `Normalize(devFolder)` — trims all string fields
- `IsSameDevFolder()` — OrdinalIgnoreCase match on both path components
- `Clone()` — JSON round-trip clone
- `GetPath()` — `<QuasarDir>/dev-folders.json`

## Dependencies
- `Quasar/Models/QuasarDevFolderSelection.cs`
- `Magnetar.Protocol.Runtime.MagnetarPaths`
- `Magnetar.Protocol.Runtime.AtomicFileWriter`
- `System.Text.Json`

## Notes
- No file-system watcher; changes are only detected within the same process.
- Thread safety: `_devFolders` list is guarded by `_sync`.
- Unlike the profile and template catalogs, no history / archiving is performed on delete.
