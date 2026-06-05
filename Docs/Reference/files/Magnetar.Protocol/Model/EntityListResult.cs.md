# Magnetar.Protocol/Model/EntityListResult.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Response DTO for the `ServerCommandType.ListEntities` command. Returns the paged entity list together with pre-paging and total entity counts, serialized as JSON into `ServerCommandResult.Payload`.

## Structure
Namespace: `Magnetar.Protocol.Model`

Class `EntityListResult` (concrete, no base type):

| Property | Type | Description |
|---|---|---|
| `Entities` | `List<EntitySummary>` | The page of entities matching the filter after offset/limit. |
| `TotalCount` | `int` | Total matching entities before paging. |
| `TotalEntityCount` | `int` | Total live entities on the server before filtering. |
| `CapturedAtUtc` | `DateTimeOffset` | Snapshot timestamp (defaults to `UtcNow`). |

## Dependencies
- [`Magnetar.Protocol/Model/EntitySummary.cs`](EntitySummary.cs.md)
- [`Magnetar.Protocol/Transport/ServerCommandResult.cs`](../Transport/ServerCommandResult.cs.md) — `Payload` carries the serialized form.
- [`Magnetar.Protocol/Transport/ServerCommandType.cs`](../Transport/ServerCommandType.cs.md) — paired with `ListEntities`.
- [`Magnetar.Protocol/Model/EntityListFilter.cs`](EntityListFilter.cs.md) — corresponding request type.
