# Quasar.Services.PluginSdk — Plugin Config & Log Bridge

*Module `Quasar.Services.PluginSdk` — 4 files.* See the [handbook TOC](../TOC.md) and the [file Index](../Index.md).

Bridge to the Magnetar PluginSdk. `PluginConfigService` (hosted) caches per-agent plugin-config snapshots and routes update requests back to the originating agent over the wire, evicting stale entries when agents disconnect. Its DTOs mirror the PluginSdk config envelope and schema. `PluginLogStream` parses structured plugin log lines and keeps a bounded ring buffer per instance for the UI's log panels.

## Files

| File | Kind | Summary |
| --- | --- | --- |
| [Quasar/Services/PluginSdk/PluginConfigDtos.cs](../files/Quasar/Services/PluginSdk/PluginConfigDtos.cs.md) | class | Quasar-side POCOs that mirror the `ConfigStorage.SaveJson` envelope and `ConfigSchema` document produced by Magnetar's PluginSdk. These DTOs allow the Blazor config editor to deserialise and render a plugin's schema-driven UI without taking a direct dependency on the PluginSdk assembly. Field names match the SDK's camelCase JSON and are bound case-insensitively by `System.Text.Json` (Web defaults). |
| [Quasar/Services/PluginSdk/PluginConfigService.cs](../files/Quasar/Services/PluginSdk/PluginConfigService.cs.md) | class | `IHostedService` that caches the plugin configuration snapshots reported by connected agents and routes config-update commands back to them over WebSocket. Subscribes to `AgentRegistry.Changed` to evict stale cache entries when agents disconnect, and raises `Changed` for Blazor reactivity. Follows the same catalog/service pattern as other Quasar runtime services. |
| [Quasar/Services/PluginSdk/PluginLogEntry.cs](../files/Quasar/Services/PluginSdk/PluginLogEntry.cs.md) | class | Immutable record-style class representing one structured log entry produced by a plugin through the PluginSdk `QuasarLogSink`. The sink writes compact JSON lines to the dedicated server's stdout; `PluginLogStream.TryParseSinkLine` parses those lines into this type. Field shape mirrors the sink JSON: `{ timestamp, level, plugin, thread, message, data?, exception? }`. |
| [Quasar/Services/PluginSdk/PluginLogStream.cs](../files/Quasar/Services/PluginSdk/PluginLogStream.cs.md) | class | In-memory ring buffer of recent plugin log entries keyed by server unique name, plus a static parser for the PluginSdk `QuasarLogSink` JSON stdout format. Blazor components subscribe to `Changed` and read entries via `GetEntries`, `GetRecent`, or `Query`. Follows the same lock-guarded, event-raising shape as other Quasar runtime services. |

## Depends on

- [Quasar.Services.Core](Quasar.Services.Core.md)
