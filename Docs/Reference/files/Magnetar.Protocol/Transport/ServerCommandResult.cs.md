# Magnetar.Protocol/Transport/ServerCommandResult.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Command response DTO sent from the agent back to the Quasar supervisor (via `AgentWireMessage.CommandResult`). Correlates to the originating `ServerCommandEnvelope` via `CommandId`, reports success/failure, and carries an optional structured JSON payload for commands that return data.

## Structure
Namespace: `Magnetar.Protocol.Transport`

Class `ServerCommandResult` (concrete, no base type):

| Property | Type | Description |
|---|---|---|
| `CommandId` | `string` | Echoed from `ServerCommandEnvelope.CommandId` for correlation. |
| `UniqueName` | `string` | Instance unique name (echoed). |
| `AgentId` | `string` | Agent connection GUID (echoed). |
| `ServerId` | `string` | SE server ID (echoed). |
| `Success` | `bool` | `true` if the command completed successfully. |
| `Message` | `string` | Human-readable result or error message. |
| `Payload` | `string` | Optional JSON response body (e.g. `EntityListResult` for `ListEntities`). |
| `CompletedAtUtc` | `DateTimeOffset` | Completion timestamp (defaults to `UtcNow`). |

## Dependencies
- [`Magnetar.Protocol/Transport/AgentWireMessage.cs`](AgentWireMessage.cs.md) — carried as `CommandResult`.
- [`Magnetar.Protocol/Transport/ServerCommandEnvelope.cs`](ServerCommandEnvelope.cs.md) — originating request.
- [`Magnetar.Protocol/Model/EntityListResult.cs`](../Model/EntityListResult.cs.md) — example `Payload` content.
