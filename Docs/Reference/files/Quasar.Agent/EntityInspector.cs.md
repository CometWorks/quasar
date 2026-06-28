# Quasar.Agent/EntityInspector.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
`EntityInspector` is an internal static helper that queries and manipulates live `MyEntity` instances on the game thread, mapping them to transport-friendly `EntitySummary` DTOs for the Quasar admin UI. It supports paginated, filtered entity listing and direct entity deletion.

## Structure
**Namespace:** `Quasar.Agent`  
**Modifiers:** internal, static

| Member | Description |
|---|---|
| `Query(EntityListFilter)` | Iterates `MyEntities`, maps each to `EntitySummary`, applies type-tag and search filters, sorts by `SizeMeters` descending, returns paginated `EntityListResult` (default limit 500, max 2000) |
| `TryDelete(long entityId, out string message)` | Looks up entity by ID, calls `entity.Close()`, returns success flag and human-readable message |
| `TryMap` (private) | Dispatches to `MapGrid`, `MapCharacter`, `MapFloating`, `MapVoxel`, or `MapOther`; skips child/attached entities |
| `MapGrid` (private) | Populates grid-specific fields: block count, PCU, size class (Large/Small), static flag, first big owner |
| `MapCharacter` (private) | Classifies as Player/Bot/Corpse, resolves owner identity |
| `MapFloating` (private) | Extracts item subtype name and stack amount |
| `MapVoxel` (private) | Detects Planet vs Asteroid from type name and skips internal `MyVoxelPhysics` planet-sector entities so they are not exposed as viewable asteroids |
| `MapOther` (private) | Falls through for standalone non-typed entities |
| `NewSummary` (private) | Fills position, AABB, `SizeMeters`, and full 4×4 world matrix |
| `ResolveOwner` (private) | Looks up `DisplayName` and `SteamId` from `MySession.Static.Players` |
| `MatchesType` / `MatchesSearch` (private) | Filter predicates; search matches `DisplayName`, `OwnerName`, or entity ID string |

## Dependencies
- `Magnetar.Protocol.Model` — `EntitySummary`, `EntityListFilter`, `EntityListResult`
- `Sandbox.Game.Entities` — `MyEntities`, `MyCubeGrid`, `MyFloatingObject`
- `Sandbox.Game.Entities.Character` — `MyCharacter`
- `Sandbox.Game.World` — `MySession`
- `VRage.Game` — `MyCubeSize`
- `VRage.Game.Entity` — `MyEntity`
- `VRage.Game.ModAPI` — `MyVoxelBase`
- `VRageMath` — `MatrixD`

## Notes
Every public member must be called on the game thread. All game-object accesses inside `NewSummary` and the mapper helpers are wrapped in individual `try/catch` blocks so one bad entity does not abort the full query. Entity deletion calls `entity.Close()`, which is the standard SE way to remove an entity from the scene.
