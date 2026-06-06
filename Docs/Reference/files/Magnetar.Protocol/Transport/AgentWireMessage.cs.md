# Magnetar.Protocol/Transport/AgentWireMessage.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
The single envelope type for every JSON message exchanged over the agent WebSocket channel between Quasar (`/ws/agent`) and the in-DS `Quasar.Agent` plugin. A `Kind` discriminator (a `WireMessageKind` constant) selects which optional payload property is populated; serialization uses camelCase with null values omitted, so only the relevant payload travels on the wire.

## Structure
Namespace `Magnetar.Protocol.Transport`; `public class AgentWireMessage`. Tagged-union-style payload bag (only one payload field non-null per message):

| Property | Type | Populated for `Kind` |
|---|---|---|
| `Kind` | `string` | discriminator (default empty) |
| `Message` | `string` | free text, e.g. the `"pong"` reply (default empty) |
| `Hello` | `AgentHello?` | `Hello` |
| `Snapshot` | `AgentSnapshot?` | `Snapshot` |
| `Command` | `ServerCommandEnvelope?` | `Command` (Quasar→agent) |
| `CommandResult` | `ServerCommandResult?` | `CommandResult` (agent→Quasar) |
| `PluginConfigSnapshot` | `PluginConfigSnapshot?` | `PluginConfigSnapshot` |
| `PluginConfigUpdateRequest` | `PluginConfigUpdateRequest?` | `PluginConfigUpdate` |
| `PluginLogs` | `PluginLogBatch?` | `PluginLogs` |

## Dependencies
- [`Magnetar.Protocol/Transport/WireMessageKind.cs`](WireMessageKind.cs.md) (discriminator values)
- `Magnetar.Protocol/Model/AgentHello.cs`
- `Magnetar.Protocol/Model/AgentSnapshot.cs`
- `Magnetar.Protocol/Transport/ServerCommandEnvelope.cs`
- `Magnetar.Protocol/Transport/ServerCommandResult.cs`
- `Magnetar.Protocol/Model/PluginConfigSnapshot.cs`
- `Magnetar.Protocol/Model/PluginConfigUpdateRequest.cs`
- [`Magnetar.Protocol/Model/PluginLogBatch.cs`](../Model/PluginLogBatch.cs.md)

## Notes
`PluginLogs` / `PluginLogBatch` is the newer plugin-log streaming payload; it carries pre-formatted sink lines rather than structured entries. `Ping`, `Pong`, and `AdminStop` messages carry no payload object — only `Kind` (and optionally `Message`).
