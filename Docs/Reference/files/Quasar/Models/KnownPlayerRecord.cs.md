# Quasar/Models/KnownPlayerRecord.cs

**Module:** Quasar.Models  **Kind:** class  **Tier:** 2

## Summary
Persistent record for a player ever observed on any managed server, stored in `KnownPlayerCatalog`. Tracks identity across multiple servers via a composite `PlayerKey`, records Steam and platform metadata, faction/admin/ban status, last ping, and first/last-seen timestamps.

## Structure
Namespace: `Quasar.Models`  
`public sealed class KnownPlayerRecord` — no base class, no interfaces.

| Member | Description |
|---|---|
| `PlayerKey` | Stable composite key uniquely identifying this player record. |
| `UniqueName` | Server's `UniqueName` this record was last observed on. |
| `ServerId` | Internal server identifier string. |
| `ServerName` | Display name of the server. |
| `WorldName` | Name of the active world/save. |
| `HostId` | Identifier of the Quasar host managing the server. |
| `HostName` | Display name of the host. |
| `SteamId` | Steam 64-bit ID. |
| `IdentityId` | SE in-game identity ID. |
| `SerialId` | SE serial player ID within the session. |
| `DisplayName` | In-game display name. |
| `PlatformDisplayName` | Platform (Steam/etc.) display name. |
| `PlatformIcon` | Icon identifier for the player's platform. |
| `GameAcronym` | Short code for the game variant (e.g. "SE"). |
| `ServiceName` | Name of the multiplayer service. |
| `FactionTag` | Faction tag at time of last observation. |
| `PromoteLevel` | SE promote/admin level string. |
| `IsAdmin` | Whether the player had admin rights. |
| `IsBanned` | Whether the player is banned. |
| `LastObservedPingMs` | Ping in milliseconds at last observation. |
| `FirstSeenUtc` | Earliest recorded presence across all servers. |
| `LastSeenUtc` | Most recent recorded presence. |
| `LastOnlineUtc` | Nullable; last time the player was actively online. |

## Dependencies
- [`Quasar/Services/KnownPlayerCatalog.cs`](../Services/KnownPlayerCatalog.cs.md) (owns and persists these records)
