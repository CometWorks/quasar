# Quasar/Services/PluginSdk/PluginLogStream.cs

**Module:** Quasar.Services.PluginSdk  **Kind:** class  **Tier:** 2

## Summary

In-memory ring buffer of recent plugin log entries keyed by server unique name, plus a static parser for the PluginSdk `QuasarLogSink` JSON stdout format. Blazor components subscribe to `Changed` and read entries via `GetEntries`, `GetRecent`, or `Query`. Follows the same lock-guarded, event-raising shape as other Quasar runtime services.

## Structure

Namespace: `Quasar.Services.PluginSdk`

**`PluginLogStream`** (sealed class)

Constant:
- `MaxEntriesPerServer = 10_000` — per-server ring cap

Event:
- `Changed : Action?` — fired after `Append` or `Clear`

Methods:
- `Append(PluginLogEntry)` — enqueues entry, evicts oldest beyond cap, fires `Changed`
- `GetEntries(string uniqueName) : IReadOnlyList<PluginLogEntry>` — all entries for one server, oldest first
- `GetRecent(int limit = 200) : IReadOnlyList<PluginLogEntry>` — cross-server, newest-first, capped
- `GetUniqueNames() : IReadOnlyList<string>` — sorted list of all server names with buffered entries
- `Query(PluginLogQuery) : IReadOnlyList<PluginLogEntry>` — filtered query supporting `UniqueName`, `Level`, `Text` (substring in plugin/message/exception), `FromUtc`, `ToUtc`, `Limit`; returns newest-first
- `Clear(string uniqueName)` — drops the queue for one server; fires `Changed`
- `TryParseSinkLine(string uniqueName, string? line, out PluginLogEntry? entry) : bool` (static) — cheap pre-filter (`{` prefix + `"plugin"` substring check), then full `JsonDocument` parse; returns `false` for ordinary stdout lines

**`PluginLogQuery`** (sealed record) — query parameters
- `UniqueName`, `Level`, `Text` (substring), `FromUtc` (default: UTC-24h), `ToUtc`, `Limit` (default 10,000; clamped to `MaxEntriesPerServer`)

## Dependencies

- [`Quasar/Services/PluginSdk/PluginLogEntry.cs`](PluginLogEntry.cs.md)
- BCL: `System.Text.Json`

## Notes

- All reads and writes to `_byUniqueName` acquire `_sync`. `Changed` is invoked outside the lock.
- `TryParseSinkLine` uses a two-stage filter (cheap string check, then `JsonDocument`) to avoid full JSON parsing cost for normal game log output, which represents the vast majority of stdout lines.
- Storage is per-server `Queue<PluginLogEntry>`; `GetRecent` and `Query` do an in-memory cross-queue scan under lock, which is acceptable for the expected entry counts.
