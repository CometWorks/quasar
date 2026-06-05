# Quasar/Services/Auth/RbacConfigCatalog.cs

**Module:** Quasar.Services.Auth  **Kind:** class  **Tier:** 2

## Summary
Singleton service that owns the live RBAC configuration, persisting it to `rbac.json` via atomic writes and reloading it automatically when the file is changed externally. Provides thread-safe read access and fires a `Changed` event on every meaningful config update.

## Structure
Namespace: `Quasar.Services.Auth`

`sealed class RbacConfigCatalog : IDisposable`

Constructor: `(ILogger<RbacConfigCatalog> logger)` — loads config from disk and starts a `FileSystemWatcher`.

Events:
- `Changed : Action?` — raised after any successful reload or save that changed the snapshot

Public members:
- `GetConfig() : RbacConfig` — returns a deep clone of the current config (lock-protected)
- `SaveAsync(RbacConfig, CancellationToken) : Task` — normalises, serialises, atomic-writes to disk, updates in-memory config, fires `Changed`
- `GetSubjectRoles(string provider, string subject) : IReadOnlyList<string>` — returns sorted distinct roles for the provider/subject pair (lock-protected)
- `Dispose()` — cancels debounce and disposes the watcher

Private internals:
- `LoadConfig()` — reads and normalises from `MagnetarPaths.GetQuasarDirectory()/rbac.json`
- `StartWatching()` — sets up `FileSystemWatcher` on the config directory
- `ScheduleReload()` — debounces file-system events with 250 ms delay using a `CancellationTokenSource`
- `ReloadFromDisk()` — compares JSON snapshot to avoid spurious `Changed` notifications
- `GetPath()` — `MagnetarPaths.GetQuasarDirectory() + "/rbac.json"`

## Dependencies
- [`Quasar/Services/Auth/RbacConfig.cs`](RbacConfig.cs.md) — `RbacConfig`, `SubjectRoleMapping`
- `Magnetar.Protocol.Runtime` — `MagnetarPaths`, `AtomicFileWriter`
- `System.Text.Json`

## Notes
All in-memory state (`_config`, `_snapshot`, `_reloadDebounce`) is guarded by `_sync`. The snapshot-comparison pattern prevents spurious `Changed` events when the file content is unchanged (e.g. no-op saves by external tools). `FileSystemWatcher` events are debounced at 250 ms to handle editors that write in multiple steps.
