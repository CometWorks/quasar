# Quasar/Services/EntityService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
`EntityService` issues live entity queries and deletions to a connected Quasar.Agent by routing request/response commands through `AgentRegistry.SendCommandAndWaitAsync`. It serialises filter/request payloads to JSON and deserialises the agent's result payload back to typed models.

## Structure
Namespace: `Quasar.Services`

**`EntityService`** — `sealed class`

| Member | Notes |
|--------|-------|
| `GetEntitiesAsync(AgentRuntimeState, EntityListFilter, CancellationToken)` | Sends `ServerCommandType.ListEntities` with a JSON `EntityListFilter` payload; throws on failure; returns `EntityListResult` |
| `DeleteEntityAsync(AgentRuntimeState, long entityId, CancellationToken)` | Sends `ServerCommandType.DeleteEntity` with `EntityDeleteRequest` payload; returns raw `ServerCommandResult` |
| `BuildCommand(AgentRuntimeState, ServerCommandType, string)` | Private: constructs `ServerCommandEnvelope` with agent/server identifiers and payload |

Both operations time out after 15 seconds (`QueryTimeout`, `DeleteTimeout`).

## Dependencies
- [`Quasar/Services/AgentRegistry.cs`](AgentRegistry.cs.md) — `SendCommandAndWaitAsync`
- `Magnetar.Protocol.Model` — `AgentRuntimeState`, `EntityListFilter`, `EntityListResult`, `EntityDeleteRequest`
- `Magnetar.Protocol.Transport` — `ServerCommandEnvelope`, `ServerCommandType`, `ServerCommandResult`
