# Quasar/Services/DedicatedServerCatalog.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`DedicatedServerCatalog` is the authoritative, persisted registry of all `DedicatedServerDefinition` entries managed by Quasar. It loads definitions from `server.json` files on disk at startup, watches the directory for external edits (debounced 250 ms reload), provides thread-safe upsert/delete with atomic file writes, maintains a history archive of every change, and fires a `Changed` event consumed by the supervisor and UI.

## Structure

Namespace: `Quasar.Services`

**`DedicatedServerCatalog`** — sealed class implementing `IDisposable`.

| Member | Description |
|---|---|
| `event Action? Changed` | Fires when the in-memory catalog changes (startup, reload, write, delete). |
| `Dispose()` | Disposes the `FileSystemWatcher` and debounce CTS. |
| `GetServers()` | Returns a cloned, alphabetically-sorted list of all `DedicatedServerDefinition` entries. |
| `GetServer(uniqueName)` | Finds a single definition by unique name (case-insensitive). Returns `null` if not found. |
| `UpsertAsync(definition, ct)` | Validates, normalizes, renames storage directory if unique name changed, writes atomically to `server.json`, appends a timestamped history copy, then reloads. |
| `NormalizeProfilerMode(string?)` (static) | Canonicalises profiler mode text/aliases to `Off`, `SafeContinuous`, or `DeepContinuous` with `SafeContinuous` fallback. |
| `SetGoalStateAsync(uniqueName, goalState, ct)` | Convenience: reads definition, updates `GoalState`/`AutoStart`, calls `UpsertAsync`. |
| `DeleteAsync(uniqueName, ct)` | Archives current `server.json` as `{timestamp}-deleted.json` in history, then deletes it and reloads. |

**Internal:**

- `LoadServers()` / `LoadServerDefinition(path)` — loads all `server.json` under the servers directory; migrates legacy `worldProfileId` field inline.
- `Normalize(server)` — validates unique name (regex `^[a-zA-Z0-9_-]+$`), normalizes UI and in-game names, fills default paths via `MagnetarPaths`, canonicalises `AgentProfilerMode`, clamps numeric fields, defaults/clamps `DsLogFilesToKeep`, syncs `AutoStart`/`GoalState`. Re-stores the canonical form of a valid `CpuAffinity` (via `CpuAffinitySpec.TryParse` + `Format`) and drops any invalid value (or one with fewer than the required cores) back to empty ("no affinity"), so a bad persisted value can't wedge process startup. Definitions missing `DisableImplicitMagnetarModLoad` deserialize to its model default of false.
- `PrepareStorageForSave(definition, previousUniqueName)` — handles server rename: rewrites managed sub-paths, moves directory via `Directory.Move`.
- `StartWatching()` / `ScheduleReload()` / `ReloadFromDisk()` — `FileSystemWatcher` on `*.json` in the servers directory; debounced reload compares a JSON snapshot to suppress no-op notifications.
- `SaveServerAsync` — writes atomically via `AtomicFileWriter` to `server.json` and to a history directory copy.

## Dependencies

- [`Quasar/Models/DedicatedServerDefinition.cs`](../Models/DedicatedServerDefinition.cs.md) — the definition model written/read as JSON
- [`Quasar/Models/CpuAffinitySpec.cs`](../Models/CpuAffinitySpec.cs.md) — canonicalises/validates the `CpuAffinity` field on normalize
- `Quasar/Services/AtomicFileWriter.cs` — all atomic file writes
- `Magnetar.Protocol.Runtime` — `MagnetarPaths` for directory resolution

## Notes

Unique names are validated against `^[a-zA-Z0-9_-]+$`. On rename, all managed sub-paths that fall under the old server directory are rewritten to the new directory, and `Directory.Move` is used for the root. The file-system watcher debounce uses a cancellable `Task.Delay(250ms)` pattern; rapid external edits collapse into a single reload. The snapshot comparison (JSON-serialised sorted list) suppresses spurious `Changed` events when the on-disk content is unchanged. The in-game name fields, `CpuAffinity`, `DisableImplicitMagnetarModLoad`, and `DsLogFilesToKeep` are carried through the definition clone.
