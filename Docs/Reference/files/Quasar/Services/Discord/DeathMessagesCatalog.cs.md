# Quasar/Services/Discord/DeathMessagesCatalog.cs

**Module:** Quasar.Services.Discord  **Kind:** class  **Tier:** 2

## Summary
Singleton service that manages the persisted death-message configuration (`death-messages.json`), providing thread-safe read access, atomic saves, reset-to-defaults, and automatic hot-reload when the file is changed externally.

## Structure
Namespace: `Quasar.Services.Discord`

`sealed class DeathMessagesCatalog : IDisposable`

Constructor: `(ILogger<DeathMessagesCatalog> logger)` — loads or creates the config file and starts a `FileSystemWatcher`.

Events:
- `Changed : Action?` — raised after any save or external edit that changes the snapshot

Public members:
- `GetConfig() : DeathMessagesConfig` — returns a deep clone (lock-protected)
- `SaveAsync(DeathMessagesConfig, CancellationToken) : Task` — normalises, atomic-writes, updates in-memory state, fires `Changed`
- `ResetAsync(CancellationToken) : Task` — saves `DeathMessagesConfig.CreateDefault()` to disk
- `Dispose()` — cancels debounce and disposes the watcher

Private internals:
- `LoadOrCreateConfig()` — reads from `MagnetarPaths.GetQuasarDeathMessagesPath()`; if missing, creates a default file atomically; falls back to default on error
- `Normalize(DeathMessagesConfig?)` — ensures each message list is non-empty, filling from defaults
- `NormalizeList(List<string>?, List<string>)` — trims, removes blanks, falls back to provided default list
- `ScheduleReload()` — debounces `FileSystemWatcher` events with 250 ms delay
- `ReloadFromDisk()` — reloads and compares JSON snapshot to avoid spurious events

## Dependencies
- [`Quasar/Services/Discord/DeathMessagesConfig.cs`](DeathMessagesConfig.cs.md) — `DeathMessagesConfig`
- `Magnetar.Protocol.Runtime` — `MagnetarPaths`, `AtomicFileWriter`
- `System.Text.Json`

## Notes
Follows the same catalog pattern as `RbacConfigCatalog`: lock-protected state, JSON snapshot for change detection, and 250 ms debounce on file-system events. If the config file does not exist on startup the catalog creates it with defaults, ensuring a known-good file is always present.
