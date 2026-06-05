# Magnetar.Protocol/Model/EntityListFilter.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Request parameters for the `ServerCommandType.ListEntities` command. Supports type-tag filtering, free-text search, and offset/limit pagination. Serialized as JSON into `ServerCommandEnvelope.Payload`.

## Structure
Namespace: `Magnetar.Protocol.Model`

Class `EntityListFilter` (concrete, no base type):

| Property | Type | Default | Description |
|---|---|---|---|
| `TypeTag` | `string` | `"All"` | Entity category filter: `"All"` \| `"Grid"` \| `"Character"` \| `"Float"` \| `"Voxel"`. |
| `Search` | `string` | `""` | Free-text match against display name or entity ID; empty matches all. |
| `Limit` | `int` | `500` | Max entities to return (clamped server-side). |
| `Offset` | `int` | `0` | Number of matching entities to skip for paging. |

## Dependencies
- [`Magnetar.Protocol/Transport/ServerCommandEnvelope.cs`](../Transport/ServerCommandEnvelope.cs.md) — `Payload` carries the serialized form.
- [`Magnetar.Protocol/Transport/ServerCommandType.cs`](../Transport/ServerCommandType.cs.md) — paired with `ListEntities`.
- [`Magnetar.Protocol/Model/EntityListResult.cs`](EntityListResult.cs.md) — corresponding response type.
