# Magnetar.Protocol/Model/EntityDeleteRequest.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Minimal request DTO carrying the target entity ID for the `ServerCommandType.DeleteEntity` command. Serialized as JSON into `ServerCommandEnvelope.Payload`.

## Structure
Namespace: `Magnetar.Protocol.Model`

Class `EntityDeleteRequest` (concrete, no base type):

| Property | Type | Description |
|---|---|---|
| `EntityId` | `long` | SE entity ID to delete. |

## Dependencies
- [`Magnetar.Protocol/Transport/ServerCommandEnvelope.cs`](../Transport/ServerCommandEnvelope.cs.md) — `Payload` carries the serialized form.
- [`Magnetar.Protocol/Transport/ServerCommandType.cs`](../Transport/ServerCommandType.cs.md) — paired with `DeleteEntity`.
