# Quasar/Services/PluginSdk/PluginLogEntry.cs

**Module:** Quasar.Services.PluginSdk  **Kind:** class  **Tier:** 2

## Summary

Immutable record-style class representing one structured log entry produced by a plugin through the PluginSdk `QuasarLogSink`. The sink writes compact JSON lines to the dedicated server's stdout; `PluginLogStream.TryParseSinkLine` parses those lines into this type. Field shape mirrors the sink JSON: `{ timestamp, level, plugin, thread, message, data?, exception? }`.

## Structure

Namespace: `Quasar.Services.PluginSdk`

**`PluginLogEntry`** (sealed class) — all properties are `init`-only

| Property | Type | Description |
|---|---|---|
| `UniqueName` | `string` | Quasar unique name of the server that produced the entry |
| `TimestampUtc` | `DateTimeOffset` | Parsed from the sink's ISO-8601 timestamp |
| `Level` | `string` | Severity: Debug, Info, Warning, Error, Critical |
| `Plugin` | `string` | Name of the plugin logger |
| `ThreadId` | `int` | Managed thread id |
| `Message` | `string` | Log message text |
| `Data` | `string?` | Optional structured JSON payload (raw text) |
| `Exception` | `string?` | Optional formatted exception string |

No methods; data object only.

## Dependencies

None (no external types referenced).
