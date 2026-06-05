# Quasar/Services/QuasarWorldTemplateCatalog.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Manages the persistent catalog of Quasar world templates — pre-configured world directories (containing a `Sandbox.sbc`) that can be used as starting points when creating new server instances. Templates are stored under `<QuasarDir>/WorldTemplates/<id>/` with a `template.json` metadata file and a `World/` subdirectory for the game files. The catalog supports import (copy from an arbitrary path), deletion (with archiving), file-system watching with debounced reload, and a one-time legacy migration from the old `WorldProfiles` storage layout.

## Structure
**Namespace:** `Quasar.Services`

**Type:** `QuasarWorldTemplateCatalog` (sealed class, implements `IDisposable`)

| Member | Description |
|---|---|
| `event Action? Changed` | Raised on any mutation or external-edit reload. |
| `GetTemplates()` | Returns defensive clones sorted by name then id. |
| `GetTemplate(worldTemplateId)` | Returns clone by id (OrdinalIgnoreCase), or null. |
| `GetWorldDirectory(worldTemplateId)` | Returns the expected world-files directory path (no existence check). |
| `ImportAsync(name, description, sourcePath, ct)` | Validates source contains `Sandbox.sbc`, copies all files into managed storage, saves `template.json`. |
| `DeleteAsync(worldTemplateId, ct)` | Archives metadata to history, deletes metadata file, recursively deletes world directory. |
| `Dispose()` | Stops watcher and cancels debounce. |

Private:
- `LoadTemplates()` / `LoadTemplate(path)` — reads `template.json` (and legacy `profile.json`) files; ID is authoritative from directory name
- `SaveTemplateAsync()` / `ArchiveAndDeleteTemplateAsync()` — atomic write + history
- `MigrateLegacyStorage()` — moves `WorldProfiles` → `WorldTemplates` directory, renames `profile.json` → `template.json`, rewrites legacy `worldProfileId` field to `worldTemplateId`
- `StartWatching()` / `ScheduleReload()` / `ReloadFromDisk()` — 250 ms debounced reload on `template.json` change

## Dependencies
- [`Quasar/Models/QuasarWorldTemplate.cs`](../Models/QuasarWorldTemplate.cs.md)
- `Magnetar.Protocol.Runtime.MagnetarPaths` (all path resolution)
- `Magnetar.Protocol.Runtime.AtomicFileWriter`
- `System.Text.Json`

## Notes
- The template ID is always derived from the directory name (not from the JSON body), making the filesystem structure authoritative; the JSON is rewritten during migration if IDs are inconsistent.
- Legacy migration handles three scenarios: full directory rename, partial merge (both old and new directories exist), and per-file field rewriting.
- `WriteTextReplacing` uses a temp-then-move pattern for atomic inline replacement during migration (distinct from `AtomicFileWriter` which is used for normal saves).
- World directory is deleted recursively on template deletion — ensure the template ID is correct before calling.
