# Magnetar.Protocol/Model/DeathEventSnapshot.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Sealed DTO representing a single player death event captured for transmission in `AgentSnapshot.RecentDeaths`. Carries victim, optional killer and weapon, death classification, and timestamp.

## Structure
Namespace: `Magnetar.Protocol.Model`

Class `DeathEventSnapshot` (sealed, no base type):

| Property | Type | Description |
|---|---|---|
| `VictimName` | `string` | Display name of the player who died. |
| `KillerName` | `string?` | Display name of the killer; `null` for environment deaths. |
| `WeaponName` | `string?` | Weapon or damage source name; `null` if not applicable. |
| `DeathType` | `string` | Categorisation string (default `"Unknown"`). |
| `TimestampTicksUtc` | `long` | `DateTime.Ticks` (UTC) of the event. |

## Dependencies
- [`Magnetar.Protocol/Model/AgentSnapshot.cs`](AgentSnapshot.cs.md) — listed in `RecentDeaths`.
