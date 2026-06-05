# Quasar/Services/KnownPlayerCatalog.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`KnownPlayerCatalog` accumulates and persists a historical record of every player seen across all managed dedicated servers. It is updated from `AgentSnapshot` telemetry and from successful command outcomes (ban/unban/promote/demote), deduplicates by `{uniqueName}::{steamId}` key, and saves to `known-players.json` with a 500 ms debounce.

## Structure

Namespace: `Quasar.Services`

**`KnownPlayerCatalog`** — sealed class.

| Member | Description |
|---|---|
| `event Action? Changed` | Raised after any player record mutation. |
| `GetPlayers()` | Returns a cloned list sorted by server name, display name, Steam ID. |
| `ObserveSnapshot(AgentSnapshot)` | Upserts player records from a snapshot's `Players` list; updates identity/faction/ping fields; advances `LastSeenUtc` and `LastOnlineUtc`. |
| `ApplyCommandOutcome(ServerCommandEnvelope, ServerCommandResult)` | On successful `BanPlayer`/`UnbanPlayer`/`PromotePlayer`/`DemotePlayer`/`SetPlayerPromoteLevel`, updates `IsBanned`/`PromoteLevel`/`IsAdmin`. |

Internal helpers:
- `ApplySnapshot` / `ApplyCommand` — field-level change detection via generic `Assign<T>` helper (returns `true` if changed).
- `GetAdjacentPromoteLevel` / `NormalizePromoteLevel` — navigate the `["None","Scripter","Moderator","SpaceMaster","Admin"]` ladder.
- `ScheduleSave` / `SaveAsync` — 500 ms debounced atomic JSON write to `known-players.json`.

Player display names are sanitised through `TextSanitizer.CleanGameText` on both store and retrieve.

## Dependencies

- `Quasar/Services/AtomicFileWriter.cs` — atomic persistence
- [`Quasar/Models/KnownPlayerRecord.cs`](../Models/KnownPlayerRecord.cs.md) — the persisted record model
- `Quasar/Models/TextSanitizer.cs` — `CleanGameText`
- `Magnetar.Protocol.Model` — `AgentSnapshot`, `PlayerSnapshot`
- `Magnetar.Protocol.Transport` — `ServerCommandEnvelope`, `ServerCommandResult`, `ServerCommandType`
- `Magnetar.Protocol.Runtime` — `MagnetarPaths`

## Notes

`LastSeenUtc` is advanced only if the new observation is at least 1 minute newer than the stored value, preventing save-debounce thrashing on every snapshot tick. Ban/promote state is applied optimistically from command outcomes before the next snapshot arrives.
