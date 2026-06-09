# Magnetar.Protocol/Transport/ServerCommandType.cs

**Module:** Magnetar.Protocol  **Kind:** enum  **Tier:** 1

## Summary
Discriminator enum embedded in `ServerCommandEnvelope.CommandType` that identifies which server-side action the Quasar supervisor is requesting from an agent.

## Structure
Namespace: `Magnetar.Protocol.Transport`

Enum `ServerCommandType` (underlying type: `int`):

| Value | Name | Description |
|---|---|---|
| 0 | `Unknown` | Default/unset; should not be dispatched. |
| 1 | `Refresh` | Request an immediate `AgentSnapshot` push. |
| 2 | `SendChat` | Broadcast a chat message (text in `Text`). |
| 3 | `SaveWorld` | Trigger a world save. |
| 4 | `StopServer` | Gracefully stop the SE dedicated server. |
| 5 | `KickPlayer` | Kick a player (target in `SteamId`). |
| 6 | `BanPlayer` | Ban a player (target in `SteamId`). |
| 7 | `UnbanPlayer` | Unban a player (target in `SteamId`). |
| 8 | `PromotePlayer` | Promote a player one level (target in `SteamId`). |
| 9 | `DemotePlayer` | Demote a player one level (target in `SteamId`). |
| 10 | `ListEntities` | List world entities; filter in `Payload` as `EntityListFilter`. |
| 11 | `DeleteEntity` | Delete an entity; target in `Payload` as `EntityDeleteRequest`. |
| 12 | `SetPlayerPromoteLevel` | Set an explicit promote level; parameters in `Payload`. |
| 13 | `ClearKickCooldown` | Clear a player's server-side kick cooldown (target in `SteamId`). |
| 14 | `SetProfilerMode` | Change the agent profiler mode live; mode text in `Text`. |

## Dependencies
- [`Magnetar.Protocol/Transport/ServerCommandEnvelope.cs`](ServerCommandEnvelope.cs.md) — `CommandType` field.
- [`Magnetar.Protocol/Model/EntityListFilter.cs`](../Model/EntityListFilter.cs.md) — payload for `ListEntities`.
- [`Magnetar.Protocol/Model/EntityDeleteRequest.cs`](../Model/EntityDeleteRequest.cs.md) — payload for `DeleteEntity`.
