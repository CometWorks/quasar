# Quasar/Services/Discord/DiscordOptionsCatalog.cs

**Module:** Quasar.Services.Discord  **Kind:** class  **Tier:** 2

## Summary
Singleton service that owns the live Discord options, persisting them to `discord-options.json` via atomic writes and hot-reloading when the file is changed externally. Mirrors the catalog pattern of `RbacConfigCatalog` and `DeathMessagesCatalog`.

## Structure
Namespace: `Quasar.Services.Discord`

`sealed class DiscordOptionsCatalog : IDisposable`

Constructor: `(ILogger<DiscordOptionsCatalog> logger)` — loads options from disk and starts `FileSystemWatcher`.

Events:
- `Changed : Action?` — raised after any save or external edit that changes the JSON snapshot

Public members:
- `GetOptions() : DiscordOptions` — deep clone of the current options (lock-protected)
- `SaveAsync(DiscordOptions, CancellationToken) : Task` — normalises, atomic-writes, updates in-memory state, fires `Changed`
- `Dispose()` — cancels debounce, disposes watcher

Private internals:
- `LoadOptions()` — reads from `MagnetarPaths.GetQuasarDiscordOptionsPath()`; returns normalised empty defaults if file absent or corrupt
- `StartWatching()` — creates `FileSystemWatcher` on the config directory, filter on the exact file name
- `ScheduleReload()` — debounces with 250 ms delay
- `ReloadFromDisk()` — snapshot comparison to skip spurious events
- `CreateSnapshot(DiscordOptions)` — serialises a normalised copy for comparison

## Dependencies
- [`Quasar/Services/Discord/DiscordOptions.cs`](DiscordOptions.cs.md) — `DiscordOptions`
- `Magnetar.Protocol.Runtime` — `MagnetarPaths`, `AtomicFileWriter`
- `System.Text.Json`

## Notes
Identical structural pattern to `RbacConfigCatalog` and `DeathMessagesCatalog`: lock-guarded state, JSON snapshot for change detection, 250 ms debounced `FileSystemWatcher`. `DiscordBotService` subscribes to `Changed` to trigger bot restarts when configuration is updated at runtime.
