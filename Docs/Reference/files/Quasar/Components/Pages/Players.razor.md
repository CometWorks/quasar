# Quasar/Components/Pages/Players.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page at `/players` listing all known players observed across managed servers. Merges the persisted `KnownPlayerCatalog` with live agent snapshots to show online/offline status, role, faction, service and Steam id, and offers per-row moderation actions (set promote level, kick, clear-kick-cooldown, ban/unban) dispatched to the owning agent. While a kicked player's cooldown is active it shows a warning "kicked (Ns)" countdown chip; the expiry (`KickCooldownExpiresUtc`) is derived from the agent snapshot's `KickedPlayers` (converting `RemainingCooldownMs` to an absolute time) so the countdown keeps ticking between ~1s snapshot refreshes.

## Structure
- **`@page "/players"`**, **`@implements IDisposable`**
- **`[Inject]`**: `AgentRegistry Registry`, `DedicatedServerCatalog ServerCatalog`, `KnownPlayerCatalog KnownPlayers`, `ISnackbar Snackbar`
- **Key UI**
  - Header text plus a search/stats `MudStack`: search `MudTextField` bound to `_searchText` (immediate, clearable) and chips for Known / Online / Shown (Shown only while searching).
  - Main `MudPaper` shows an info `MudAlert` when there are no known players or no search match, otherwise a sortable `MudTable<KnownPlayerView>` with columns Server, Player, Service, Steam ID, Faction, Role, Last Seen, Status, and a trailing actions menu.
  - Status cell chips: online/offline, `banned` (when `Record.IsBanned`), and `agent offline` (when `!CanModerate`).
  - Actions `MudMenu` (disabled when `!CanModerate`): disabled "Set role" header, one item per promote level, a divider, then Kick (online only) and Ban/Unban.
- **`PromoteLevels`** static array: None, Scripter, Moderator, SpaceMaster, Admin.
- **Live-render guard:** `_menuOpen` tracks an open row menu; `ShouldRender()` returns `!_menuOpen` so live data pushes do not tear down the open popup.
- **`BuildKnownPlayerViews`** — builds dictionaries of connected agents by `UniqueNameKey`, online players keyed by `uniqueName::steamId` (`BuildPlayerKey`), and configured server names (`DedicatedServerDefinition.DisplayName` keyed by unique name), joins each `KnownPlayerRecord` to its agent, online `PlayerSnapshot`, and configured name, then orders online-first then by server / player name / Steam id.
- **`FilteredPlayers`** — `KnownPlayerViews` filtered by `MatchesSearch` (server name, unique name, player/platform name, service, faction, Steam id).
- **`SendPlayerCommandAsync(view, ServerCommandType, text="")`** — sends a `ServerCommandEnvelope` (UniqueName, AgentId, ServerId, CommandType, Text, SteamId) via `Registry.SendCommandAsync`; snackbars on missing agent or exception.
- **Helpers:** `GetPlayerName` / `GetPlatformName` (via `TextSanitizer.CleanGameText`), `GetRoleLabel`, `IsCurrentPromoteLevel`, `GetServiceLabel`, `FormatLastSeen`, `Contains`.
- **`KnownPlayerView` (private sealed class)** — `Record`, `Agent`, `OnlinePlayer`, `ConfiguredServerName` (the server's configured `DisplayName`, or null when no matching definition); computed `IsOnline`, `CanModerate` (`Agent?.IsConnected`), `ServerDisplayName` (configured server name → agent display name → record server name → unique name).
- Subscribes to `Registry.Changed`, `ServerCatalog.Changed`, and `KnownPlayers.Changed` in `OnInitialized`, unsubscribes in `Dispose`; `HandleChanged` marshals `StateHasChanged` via `InvokeAsync`.

## Dependencies
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md) — agents, snapshots, `SendCommandAsync`, `AgentRuntimeState`
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md) — configured server display names
- [`Quasar/Services/KnownPlayerCatalog.cs`](../../Services/KnownPlayerCatalog.cs.md) — persisted `KnownPlayerRecord` set
- `Quasar/Utilities/TextSanitizer.cs` — game-text cleaning
- `Magnetar.Protocol` — `PlayerSnapshot`, `ServerCommandEnvelope`, `ServerCommandType`
- MudBlazor — `MudTable`, `MudMenu`, `MudMenuItem`, `MudChip`, `MudDivider`, `ISnackbar`

## Notes
- Moderation requires a connected agent; `CanModerate` gates the menu and `SendPlayerCommandAsync` reports an error if the agent disconnected meanwhile.
- The `ShouldRender` suppression is deliberate: the live feed pushes frequent updates that would otherwise close an open action menu mid-interaction.
- Player and platform names pass through `TextSanitizer.CleanGameText` to strip control characters in in-game names; platform name is hidden when it equals the display name.
