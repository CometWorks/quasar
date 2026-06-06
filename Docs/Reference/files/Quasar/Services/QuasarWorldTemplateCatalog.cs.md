# Quasar/Services/QuasarWorldTemplateCatalog.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Manages the persistent catalog of Quasar world templates — pre-configured world directories (each containing a `Sandbox.sbc`) used as starting points when seeding new server worlds. Templates live under `<QuasarDir>/WorldTemplates/<id>/` with a `template.json` metadata file and a `World/` subdirectory of game files. Supports import (copy from an arbitrary path), deletion (with history archiving), debounced file-system watching, and a one-time legacy migration from the old `WorldProfiles` layout.

## Structure
**Namespace:** `Quasar.Services`

**Type:** `QuasarWorldTemplateCatalog` — sealed class, implements `IDisposable`. Holds the template list, a JSON snapshot for change detection, a `FileSystemWatcher`, and a reload-debounce CTS, all guarded by a `_sync` lock.

| Member | Description |
|---|---|
| `event Action? Changed` | Raised on any mutation or external-edit reload. |
| `GetTemplates()` | Defensive clones, sorted by name then id. |
| `GetTemplate(worldTemplateId)` | Clone by id (OrdinalIgnoreCase) or null. |
| `GetWorldDirectory(worldTemplateId)` | Expected world-files directory path (no existence check). |
| `ImportAsync(name, description, sourcePath, ct)` | Validates source has `Sandbox.sbc`, copies all files (skipping the world's `Backup/` subdir) into managed storage, saves `template.json`. |
| `DeleteAsync(worldTemplateId, ct)` | Archives metadata to history, deletes the metadata file, recursively deletes the world directory. |
| `Dispose()` | Disposes watcher and cancels the debounce CTS. |

Private: `LoadTemplates` / `LoadTemplate` (reads `template.json`, also legacy `profile.json`; id is authoritative from the directory name); `SaveTemplateAsync` / `ArchiveAndDeleteTemplateAsync` (atomic write + timestamped history); `MigrateLegacyStorage` / `RenameLegacyTemplateDefinitions` / `RewriteLegacyTemplateDefinitions`; `StartWatching` / `ScheduleReload` / `ReloadFromDisk` (250 ms debounced reload on `template.json` changes).

## Dependencies
- `Quasar/Models/QuasarWorldTemplate.cs` — the model
- `Quasar/Services/AtomicFileWriter.cs` — atomic saves and history writes
- `Magnetar.Protocol.Runtime.MagnetarPaths` — all template/world/history path resolution
- `System.Text.Json`

## Notes
- The template ID is always derived from the directory name (not the JSON body), making the filesystem authoritative; migration rewrites the JSON when ids disagree.
- Legacy migration handles three cases: full directory rename, partial merge (both dirs exist), and per-file `worldProfileId` → `worldTemplateId` field rewrite (`profile.json` → `template.json`).
- `WriteTextReplacing` uses temp-then-move for atomic inline rewriting during migration (distinct from `AtomicFileWriter` used for normal saves).
- Import skips the source world's `Backup/` subdirectory (historical save copies). The world directory is recursively deleted on template deletion.
