# Quasar/Services/QuasarConfigProfileCatalog.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Manages the persistent catalog of named Quasar configuration profiles (reusable bundles of root/session settings, plugin selections, and mod lists). Profiles are stored as individual `profile.json` files under `<QuasarDir>/ConfigProfiles/<id>/`. The catalog watches the directory for external edits, debounces reload events, keeps versioned history on every save, and fires a `Changed` event when the in-memory state diverges from disk.

## Structure
**Namespace:** `Quasar.Services`

**Type:** `QuasarConfigProfileCatalog` (sealed class, implements `IDisposable`)

Notable members:
| Member | Description |
|---|---|
| `event Action? Changed` | Raised on any in-memory change (upsert, delete, or external-edit reload). |
| `GetProfiles()` | Returns defensive clones sorted by name then id. |
| `GetProfile(configProfileId)` | Returns a defensive clone by id (OrdinalIgnoreCase), or null. |
| `UpsertAsync(profile, ct)` | Normalizes, updates in-memory list, saves atomically, appends timestamped history. |
| `DeleteAsync(configProfileId, ct)` | Removes from memory, archives current file to history, deletes main file. |
| `Dispose()` | Stops the file-system watcher and cancels debounce. |

Private helpers:
- `LoadProfiles()` / `LoadProfile(path)` — deserializes from disk; returns default profiles if directory missing
- `CreateDefaultProfiles()` — seeds two starter profiles (Survival, Creative) on first run
- `SaveProfileAsync()` — atomic write + history append using `AtomicFileWriter`
- `ArchiveAndDeleteCurrentProfileAsync()` — archive-then-delete on removal
- `Normalize(profile)` — trims strings, deduplicates admins/reserved/banned/plugins/mods, ensures non-null sub-objects
- `Clone(profile)` — JSON round-trip clone
- `StartWatching()` / `ScheduleReload()` / `ReloadFromDisk()` — 250 ms debounced file watcher that only reloads on content change (snapshot comparison)

## Dependencies
- [`Quasar/Models/QuasarConfigProfile.cs`](../Models/QuasarConfigProfile.cs.md)
- `Quasar/Models/QuasarWorldRootSettings.cs`
- `Quasar/Models/QuasarSessionSettings.cs`
- `Quasar/Models/QuasarPluginSelection.cs`
- `Quasar/Models/QuasarModSelection.cs`
- [`Quasar/Services/QuasarPluginCatalogService.cs`](QuasarPluginCatalogService.cs.md) (calls `IsManualSelectionAllowed` during normalization)
- `Magnetar.Protocol.Runtime.MagnetarPaths` (path resolution)
- `Magnetar.Protocol.Runtime.AtomicFileWriter` (safe file writes)
- `System.Text.Json` (serialization / clone)

## Notes
- Thread safety: all reads and writes of `_profiles` / `_snapshot` are guarded by `_sync` (object lock).
- History files are named `yyyyMMddHHmmssfff.json`; deleted profiles get a `-deleted.json` suffix.
- Clone is done via JSON round-trip (not shallow copy) to prevent accidental aliasing.
- File watcher only triggers reload for files literally named `profile.json` to avoid reacting to history or temp files.
