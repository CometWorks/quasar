# Magnetar.Protocol/Model/PlayerSnapshot.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
DTO representing a single online player included in `AgentSnapshot.Players`. Captures Steam/platform identity, faction, admin status, and current ping.

## Structure
Namespace: `Magnetar.Protocol.Model`

Class `PlayerSnapshot` (concrete, no base type):

| Property | Type | Description |
|---|---|---|
| `SteamId` | `long` | Steam 64-bit ID. |
| `IdentityId` | `long` | SE in-game identity ID. |
| `SerialId` | `int` | Connection serial number within the session. |
| `DisplayName` | `string` | In-game display name. |
| `PlatformDisplayName` | `string` | Platform-reported display name (may differ from in-game). |
| `PlatformIcon` | `string` | Icon identifier for the platform (Steam, Xbox, etc.). |
| `GameAcronym` | `string` | Short game identifier (e.g. `"SE"`). |
| `ServiceName` | `string` | Platform service name (e.g. `"Steam"`). |
| `FactionTag` | `string` | Current faction tag; empty if not in a faction. |
| `PromoteLevel` | `string` | Admin/moderator promote level string. |
| `IsAdmin` | `bool` | Whether the player has server admin rights. |
| `PingMs` | `int` | Measured round-trip ping in milliseconds. |

## Dependencies
- [`Magnetar.Protocol/Model/AgentSnapshot.cs`](AgentSnapshot.cs.md) — listed in `Players`.
