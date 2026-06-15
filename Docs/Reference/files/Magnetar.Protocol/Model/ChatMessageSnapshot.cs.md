# Magnetar.Protocol/Model/ChatMessageSnapshot.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Immutable-style DTO representing a single in-game chat message captured for transmission in `AgentSnapshot.RecentChat`, including whether the message was emitted by the dedicated server/Good.bot rather than a player.

## Structure
Namespace: `Magnetar.Protocol.Model`

Class `ChatMessageSnapshot` (concrete, no base type):

| Property | Type | Description |
|---|---|---|
| `SteamId` | `long` | Steam ID of the author (0 for server messages). |
| `AuthorName` | `string` | Display name at the time the message was sent. |
| `Content` | `string` | Message text. |
| `TimestampTicksUtc` | `long` | `DateTime.Ticks` (UTC) of the message. |
| `IsServerMessage` | `bool` | True for dedicated-server/Good.bot messages, including Quasar/Discord relay broadcasts sent with server SteamId 0. |

## Dependencies
- [`Magnetar.Protocol/Model/AgentSnapshot.cs`](AgentSnapshot.cs.md) — listed in `RecentChat`.
