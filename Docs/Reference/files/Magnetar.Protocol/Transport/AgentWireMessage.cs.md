# Magnetar.Protocol/Transport/AgentWireMessage.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Top-level wire envelope for all WebSocket messages exchanged between `Quasar.Agent` and the Quasar supervisor. Uses a tagged-union pattern: `Kind` (a `WireMessageKind` constant) discriminates which payload field is populated; unused fields are `null`.

## Structure
Namespace: `Magnetar.Protocol.Transport`

Class `AgentWireMessage` (concrete, no base type):

| Property | Type | Description |
|---|---|---|
| `Kind` | `string` | Discriminator matching a `WireMessageKind` constant. |
| `Message` | `string` | Optional plain-text message or error description. |
| `Hello` | `AgentHello?` | Populated for `WireMessageKind.Hello`. |
| `Snapshot` | `AgentSnapshot?` | Populated for `WireMessageKind.Snapshot`. |
| `Command` | `ServerCommandEnvelope?` | Populated for `WireMessageKind.Command` (supervisor→agent). |
| `CommandResult` | `ServerCommandResult?` | Populated for `WireMessageKind.CommandResult` (agent→supervisor). |
| `PluginConfigSnapshot` | `PluginConfigSnapshot?` | Populated for `WireMessageKind.PluginConfigSnapshot`. |
| `PluginConfigUpdateRequest` | `PluginConfigUpdateRequest?` | Populated for `WireMessageKind.PluginConfigUpdate`. |

## Dependencies
- [`Magnetar.Protocol/Transport/WireMessageKind.cs`](WireMessageKind.cs.md)
- [`Magnetar.Protocol/Model/AgentHello.cs`](../Model/AgentHello.cs.md)
- [`Magnetar.Protocol/Model/AgentSnapshot.cs`](../Model/AgentSnapshot.cs.md)
- [`Magnetar.Protocol/Transport/ServerCommandEnvelope.cs`](ServerCommandEnvelope.cs.md)
- [`Magnetar.Protocol/Transport/ServerCommandResult.cs`](ServerCommandResult.cs.md)
- [`Magnetar.Protocol/Model/PluginConfigSnapshot.cs`](../Model/PluginConfigSnapshot.cs.md)
- [`Magnetar.Protocol/Model/PluginConfigUpdateRequest.cs`](../Model/PluginConfigUpdateRequest.cs.md)

## Notes
Only one payload field should be non-null per message. Ping/Pong and AdminStop messages use only `Kind` (and optionally `Message`); all other fields remain null.
