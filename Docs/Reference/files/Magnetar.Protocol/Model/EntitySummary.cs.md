# Magnetar.Protocol/Model/EntitySummary.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
DTO representing a single live world entity returned by `ListEntities`. Position, AABB, and the full 4x4 world matrix are flattened to plain `double` fields, keeping this netstandard2.0 assembly free of any VRage math dependency while preserving enough data for a future oriented-bounding-box renderer.

## Structure
Namespace: `Magnetar.Protocol.Model`

Class `EntitySummary` (concrete, no base type):

| Property | Type | Description |
|---|---|---|
| `EntityId` | `long` | SE entity ID. |
| `DisplayName` | `string` | Entity display name. |
| `TypeTag` | `string` | `"Grid"` \| `"Character"` \| `"Float"` \| `"Voxel"` \| `"Other"`. |
| `SubType` | `string` | e.g. `"LargeStatic"`, `"LargeShip"`, `"SmallShip"`, `"Player"`, `"Bot"`, `"Asteroid"`. |
| `BlockCount` | `int?` | Block count for grids; `null` for non-grid entities. |
| `Pcu` | `int?` | PCU cost; `null` if not applicable. |
| `OwnerSteamId` | `ulong?` | Steam ID of the owner; `null` if unowned. |
| `OwnerName` | `string` | Owner display name. |
| `PositionX/Y/Z` | `double` | World-space position (equals world matrix translation row). |
| `AabbMinX/Y/Z`, `AabbMaxX/Y/Z` | `double` | World-space axis-aligned bounding box corners. |
| `SizeMeters` | `double` | Largest AABB dimension in metres (convenience for sizing/sorting). |
| `WorldMatrixM11`–`M44` | `double` (×16) | Full `VRageMath.MatrixD` row-major, orientation (Right/Up/Backward) + translation. |

## Dependencies
- [`Magnetar.Protocol/Model/EntityListResult.cs`](EntityListResult.cs.md) — contained in `Entities` list.

## Notes
The 16 world-matrix fields are intentionally included for a future world-space renderer needing oriented bounding boxes; the AABB alone only supports axis-aligned rendering.
