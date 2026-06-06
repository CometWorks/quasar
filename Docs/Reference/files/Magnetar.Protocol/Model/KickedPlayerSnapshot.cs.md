# Magnetar.Protocol/Model/KickedPlayerSnapshot.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 3

## Summary
DTO representing one player currently serving a server-side kick cooldown, transmitted in `AgentSnapshot.KickedPlayers`. Kicked players are offline, so they arrive in this separate collection rather than in `AgentSnapshot.Players`. `RemainingCooldownMs` lets the supervisor convert the entry to an absolute expiry and tick down a countdown on the Players page.

## Structure
Namespace: `Magnetar.Protocol.Model`
`public class KickedPlayerSnapshot`

| Member | Description |
|---|---|
| `SteamId` | `long` — Steam ID of the kicked player. |
| `DisplayName` | `string` — Player display name (defaults to empty). |
| `RemainingCooldownMs` | `int` — Remaining kick cooldown in milliseconds. |

## Dependencies
- Referenced by [`AgentSnapshot.cs`](AgentSnapshot.cs.md)
- No external packages.
