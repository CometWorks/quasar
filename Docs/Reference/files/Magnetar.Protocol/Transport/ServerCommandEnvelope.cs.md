# Magnetar.Protocol/Transport/ServerCommandEnvelope.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Command request DTO sent from the Quasar supervisor to an agent over the WebSocket (via `AgentWireMessage.Command`). Carries a unique correlation ID, target server routing keys, the command type, and an optional JSON payload for structured command parameters.

## Structure
Namespace: `Magnetar.Protocol.Transport`

Class `ServerCommandEnvelope` (concrete, no base type):

| Property | Type | Description |
|---|---|---|
| `CommandId` | `string` | Auto-generated `Guid.NewGuid().ToString("N")` for request/response correlation. |
| `UniqueName` | `string` | Target instance unique name. |
| `AgentId` | `string` | Target agent connection GUID. |
| `ServerId` | `string` | Target SE server ID. |
| `CommandType` | `ServerCommandType` | Enum discriminating the action. |
| `Text` | `string` | Plain-text argument (e.g. chat message body, kick reason). |
| `SteamId` | `long?` | Target player Steam ID for player-targeted commands. |
| `Payload` | `string` | Optional JSON body for structured commands (`ListEntities`, `DeleteEntity`, `SetPlayerPromoteLevel`, etc.). Empty for simple commands. |
| `IssuedAtUtc` | `DateTimeOffset` | Timestamp at construction (defaults to `UtcNow`). |

## Dependencies
- [`Magnetar.Protocol/Transport/ServerCommandType.cs`](ServerCommandType.cs.md)
- [`Magnetar.Protocol/Transport/AgentWireMessage.cs`](AgentWireMessage.cs.md) — carried as `Command`.
- [`Magnetar.Protocol/Transport/ServerCommandResult.cs`](ServerCommandResult.cs.md) — paired response (matched by `CommandId`).
- [`Magnetar.Protocol/Model/EntityListFilter.cs`](../Model/EntityListFilter.cs.md) — example `Payload` content.
- [`Magnetar.Protocol/Model/EntityDeleteRequest.cs`](../Model/EntityDeleteRequest.cs.md) — example `Payload` content.
