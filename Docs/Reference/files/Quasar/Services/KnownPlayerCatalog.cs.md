# Quasar/Services/KnownPlayerCatalog.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`KnownPlayerCatalog` accumulates and persists a historical record of every human player seen across all managed dedicated servers. It is updated from `AgentSnapshot` telemetry and from successful command outcomes (ban/unban/promote/demote), deduplicates by `{uniqueName}::{steamId}` key, prunes snapshot-reported hidden NPC/bot player ids, automatically removes records older than the configured retention window (default 30 days by `LastSeenUtc`), saves players to `known-players.json` with a 500 ms debounce, and persists retention settings in `known-player-settings.json`.

## Structure

Namespace: `Quasar.Services`

**`KnownPlayerCatalog`** — sealed class.

| Member | Description |
|---|---|
| `event Action? Changed` | Raised after any player record mutation. |
| `RetentionDays` | Current automatic cleanup retention window in days; clamped to 1-3650 and defaulting to 30. |
| `GetPlayers()` | Removes expired records, then returns a cloned list sorted by server name, display name, Steam ID. |
| `SetRetentionDaysAsync(int, CancellationToken)` | Persists the retention setting to `known-player-settings.json`, immediately removes newly expired rows, and raises `Changed` when state changes. |
| `CleanExpiredAsync(CancellationToken)` | Manually removes records older than the current retention window. |
| `CleanAllAsync(CancellationToken)` | Clears all saved known-player rows globally and persists the empty set. |
| `CleanServerAsync(string, CancellationToken)` | Clears saved known-player rows belonging to one server unique name and persists the result. |
| `ObserveSnapshot(AgentSnapshot)` | Removes expired rows, removes records matching snapshot `HiddenPlayerSteamIds` / `HiddenPlayerIdentityIds`, then upserts records from `Players`; updates identity/faction/ping fields; advances `LastSeenUtc` and `LastOnlineUtc`. |
| `ApplyCommandOutcome(ServerCommandEnvelope, ServerCommandResult)` | Removes expired rows first; on successful `BanPlayer`/`UnbanPlayer`/`PromotePlayer`/`DemotePlayer`/`SetPlayerPromoteLevel`, updates `IsBanned`/`PromoteLevel`/`IsAdmin`. |
| `void ReloadFromDisk()` | Re-reads known-player settings and players from disk, replacing the in-memory set, pruning expired rows, and firing `Changed`; used after a backup restore (this catalog has no file watcher). |

Internal helpers:
- `ApplySnapshot` / `ApplyCommand` — field-level change detection via generic `Assign<T>` helper (returns `true` if changed).
- `RemoveHiddenPlayers` — deletes persisted rows for the same server when the agent reports that a SteamId or identity id belongs to a filtered bot/NPC player.
- `RemoveExpiredPlayers` / `GetRetentionTimestamp` — deletes rows older than the retention cutoff, using `LastSeenUtc`, then `LastOnlineUtc`, then `FirstSeenUtc` as fallback.
- `LoadSettings` / `SaveSettingsAsync` — JSON persistence for `known-player-settings.json`.
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

`LastSeenUtc` is advanced only if the new observation is at least 1 minute newer than the stored value, preventing save-debounce thrashing on every snapshot tick. Ban/promote state is applied optimistically from command outcomes before the next snapshot arrives. Automatic retention is enforced on construction, `GetPlayers()`, snapshots, command outcomes, and retention-setting changes, so stale rows disappear without a separate background timer.
