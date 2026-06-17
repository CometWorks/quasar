# Quasar/Components/Pages/Players.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page at `/players` listing all known players observed across managed servers. Merges the persisted `KnownPlayerCatalog` with live agent snapshots to show online/offline status, role, faction, service and Steam id, offers server/type/search filtering plus global/per-server known-player cleanup, exposes the known-player auto-clear retention setting, and provides per-row moderation actions (set promote level, kick, clear-kick-cooldown, ban/unban) dispatched to the owning agent; Kick, Ban, and cleanup actions require confirmation first. While a kicked player's cooldown is active it shows a warning "kicked (Ns)" countdown chip; the expiry (`KickCooldownExpiresUtc`) is derived from the agent snapshot's `KickedPlayers` (converting `RemainingCooldownMs` to an absolute time) so the countdown keeps ticking between ~1s snapshot refreshes.

## Structure
- **`@page "/players"`**, **`@implements IDisposable`**
- **`[Inject]`**: `AgentRegistry Registry`, `DedicatedServerCatalog ServerCatalog`, `KnownPlayerCatalog KnownPlayers`, `ISnackbar Snackbar`, `IDialogService DialogService`
- **Key UI**
  - Header text plus a two-sided controls `MudStack`: left side has server `MudSelect` (`_serverFilter`), type `MudSelect` (`_typeFilter`: All/Online/Offline/Banned/Kick cooldown), immediate clearable search field (`_searchText`), and destructive `Clean Server` / `Clean All` buttons; right side has `Auto clear after` days (`_retentionDays`) plus `Save`.
  - Stats chip row for Known / Online / Shown (`Shown` appears when any filter/search is active).
  - Main `MudPaper` (`players-list-card`) shows an info `MudAlert` when there are no known players or no filter/search match, otherwise a sortable full-width `MudTable<KnownPlayerView>` with columns Server, Player, Service, Steam ID, Faction, Role, Last Seen, Status, and a trailing actions menu.
  - Status cell chips: online/offline, `banned` (when `Record.IsBanned`), and `agent offline` (when `!CanModerate`).
  - Actions `MudMenu` (disabled when `!CanModerate`): disabled "Set role" header, one item per promote level, a divider, then Kick (online only) and Ban/Unban.
- **`PromoteLevels`** static array: None, Scripter, Moderator, SpaceMaster, Admin.
- **Filter constants/options:** `AllServersFilter`, player type constants, `PlayerTypeOptions`.
- **Live-render guard:** `_menuOpen` tracks an open row menu; `ShouldRender()` returns `!_menuOpen` so live data pushes do not tear down the open popup.
- **`BuildKnownPlayerViews`** — builds dictionaries of connected agents by `UniqueNameKey`, online players keyed by `uniqueName::steamId` (`BuildPlayerKey`), and configured server names (`DedicatedServerDefinition.DisplayName` keyed by unique name), joins each `KnownPlayerRecord` to its agent, online `PlayerSnapshot`, and configured name, then orders online-first then by server / player name / Steam id.
- **`BuildServerFilterOptions`** — combines configured servers with server names found in known-player rows, so stale records from removed server definitions remain filterable/cleanable.
- **`FilteredPlayers`** — `KnownPlayerViews` filtered by selected server, type, and `MatchesSearch` (server name, unique name, player/platform name, service, faction, Steam id).
- **`SaveRetentionDaysAsync`** — persists the auto-clear day count through `KnownPlayerCatalog.SetRetentionDaysAsync`, reports any expired rows removed immediately, and syncs local dirty-state fields.
- **`CleanSelectedServerAsync` / `CleanAllAsync`** — confirm then call `KnownPlayerCatalog.CleanServerAsync` / `CleanAllAsync`; live players are re-recorded on later snapshots.
- **`SendPlayerCommandAsync(view, ServerCommandType, text="")`** — confirms Kick/Ban, then sends a `ServerCommandEnvelope` (UniqueName, AgentId, ServerId, CommandType, Text, SteamId) via `Registry.SendCommandAsync`; snackbars on missing agent or exception.
- **Helpers:** `MatchesFilters`, `MatchesServerFilter`, `MatchesTypeFilter`, `GetPlayerName` / `GetPlatformName` (via `TextSanitizer.CleanGameText`), `GetRoleLabel`, `IsCurrentPromoteLevel`, `GetServiceLabel`, `FormatLastSeen`, `GetSelectedServerDisplayName`, `NormalizeFilterValue`, `Contains`.
- **`KnownPlayerView` (private sealed class)** — `Record`, `Agent`, `OnlinePlayer`, `ConfiguredServerName` (the server's configured `DisplayName`, or null when no matching definition); computed `IsOnline`, `CanModerate` (`Agent?.IsConnected`), `ServerDisplayName` (configured server name → agent display name → record server name → unique name).
- **`PlayerFilterOption`** private record: value/label pair used by server/type selects.
- Subscribes to `Registry.Changed`, `ServerCatalog.Changed`, and `KnownPlayers.Changed` in `OnInitialized`, unsubscribes in `Dispose`; `HandleChanged` syncs saved retention days when the local editor is not dirty and marshals `StateHasChanged` via `InvokeAsync`.

## Dependencies
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md) — agents, snapshots, `SendCommandAsync`, `AgentRuntimeState`
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md) — configured server display names
- [`Quasar/Services/KnownPlayerCatalog.cs`](../../Services/KnownPlayerCatalog.cs.md) — persisted `KnownPlayerRecord` set
- `Quasar/Utilities/TextSanitizer.cs` — game-text cleaning
- `Magnetar.Protocol` — `PlayerSnapshot`, `ServerCommandEnvelope`, `ServerCommandType`
- MudBlazor — `MudTable`, `MudMenu`, `MudMenuItem`, `MudChip`, `MudDivider`, `ISnackbar`

## Notes
- Moderation requires a connected agent; `CanModerate` gates the menu and `SendPlayerCommandAsync` reports an error if the agent disconnected meanwhile.
- Cleanup buttons are destructive and use confirmation dialogs; they only clear saved known-player rows, not live server state.
- The `ShouldRender` suppression is deliberate: the live feed pushes frequent updates that would otherwise close an open action menu mid-interaction.
- The table sits in a vertical stack and uses global `.players-*` width rules so the known-player list expands with the page instead of being constrained by a horizontal flex row.
- Player and platform names pass through `TextSanitizer.CleanGameText` to strip control characters in in-game names; platform name is hidden when it equals the display name.
