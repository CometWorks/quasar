# Quasar/Services/PluginSdk/PluginLogStream.cs

**Module:** Quasar.Services.PluginSdk  **Kind:** class  **Tier:** 2

## Summary

In-memory ring buffer of recent plugin log entries keyed by server unique name, plus a static parser for the PluginSdk `QuasarLogSink` JSON stdout format. The supervisor feeds entries parsed from each dedicated server's standard output; Blazor components subscribe to `Changed` and read entries via `GetEntries`, `GetRecent`, or `Query`. Follows the same lock-guarded, event-raising shape as other Quasar runtime services.

## Structure

Namespace: `Quasar.Services.PluginSdk`

**`PluginLogStream`** (sealed class)

Constant:
- `MaxEntriesPerServer = 10_000` — per-server ring cap

Event:
- `Changed : Action?` — fired after `Append` and after a `Clear` that removed something

Methods:
- `Append(PluginLogEntry)` — enqueues entry into the server's queue, evicts oldest beyond cap, fires `Changed`
- `GetEntries(string uniqueName) : IReadOnlyList<PluginLogEntry>` — all entries for one server, oldest first
- `GetRecent(int limit = 200) : IReadOnlyList<PluginLogEntry>` — cross-server, newest-first, capped
- `GetUniqueNames() : IReadOnlyList<string>` — sorted list of servers with buffered entries
- `GetPlugins() : IReadOnlyList<string>` — distinct, sorted plugin names across all buffered entries
- `Query(PluginLogQuery) : IReadOnlyList<PluginLogEntry>` — filtered cross-server query (see below), newest-first, clamped to `MaxEntriesPerServer`
- `Clear(string uniqueName)` — drops the queue for one server; fires `Changed` if it existed
- `TryParseSinkLine(string uniqueName, string? line, out PluginLogEntry? entry) : bool` (static) — cheap pre-filter (`{` prefix + `"plugin"` substring), then `JsonDocument` parse requiring `timestamp`/`level`/`plugin`/`message`; optionally reads `thread`, `data` (raw JSON), `exception`; returns `false` for ordinary stdout lines

**`PluginLogQuery`** (sealed record) — query parameters
- `UniqueName`, `Level`, `Plugin`, `Text` (case-insensitive substring over plugin/message/exception)
- `FromUtc : DateTimeOffset?` (default: UTC-24h), `ToUtc : DateTimeOffset?`
- `Limit : int` (default 10,000; clamped to `MaxEntriesPerServer`)

## Dependencies

- [`Quasar/Services/PluginSdk/PluginLogEntry.cs`](PluginLogEntry.cs.md)
- External: `System.Text.Json`

## Notes

- All reads/writes to `_byUniqueName` hold `_sync`; `Changed` is invoked outside the lock.
- `TryParseSinkLine` uses a two-stage filter (cheap string check, then `JsonDocument`) to skip full JSON parsing for ordinary game stdout, which is the vast majority of lines and flows on to the plain log file.
- Storage is per-server `Queue<PluginLogEntry>`; `GetRecent`/`Query`/`GetPlugins` do an in-memory cross-queue scan under lock, acceptable for the expected entry counts.
