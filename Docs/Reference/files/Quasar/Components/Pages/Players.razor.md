# Quasar/Components/Pages/Players.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page at `/players` listing all known players across managed servers. Combines persisted `KnownPlayerRecord` data with live agent snapshots to show online/offline status, faction, role, and last-seen time, and allows moderation actions (kick, ban/unban, set promote level) via per-row action menus.

## Structure
- **`@page "/players"`**
- **`@implements IDisposable`**
- **`[Inject]`**
  - `AgentRegistry Registry`
  - `KnownPlayerCatalog KnownPlayers`
  - `ISnackbar Snackbar`
- **Key UI**
  - Search bar (`MudTextField`) + stat chips (Known / Online / Shown counts).
  - `MudTable<KnownPlayerView>` with sortable columns: Server, Player (display + platform name), Service, Steam ID, Faction, Role, Last Seen, Status.
  - Per-row `MudMenu` with promote-level submenu (None / Scripter / Moderator / SpaceMaster / Admin), Kick, and Ban/Unban items. Menu is disabled when the agent is offline (`CanModerate = false`).
- **`KnownPlayerView` (private sealed class)** — joins `KnownPlayerRecord`, optional `AgentRuntimeState`, and optional live `PlayerSnapshot`.
  - `IsOnline` — true when `OnlinePlayer` is not null.
  - `CanModerate` — true when `Agent.IsConnected`.
  - `ServerDisplayName` — cascades from agent display name → record server name → unique name.
- **`BuildKnownPlayerViews`** — constructs a lookup of connected agents and online players, then joins over `KnownPlayers.GetPlayers()`.
- **`ShouldRender`** — returns `false` while `_menuOpen` to avoid tearing down an open action menu during live updates.
- **`SendPlayerCommandAsync`** — dispatches `ServerCommandEnvelope` via `Registry.SendCommandAsync`; shows snackbar on success/error.
- **`GetPlayerName`** / **`GetPlatformName`** — sanitize game text via `TextSanitizer.CleanGameText`.

## Dependencies
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md)
- [`Quasar/Services/KnownPlayerCatalog.cs`](../../Services/KnownPlayerCatalog.cs.md)
- [`Quasar/Models/KnownPlayerRecord.cs`](../../Models/KnownPlayerRecord.cs.md)
- `Quasar/Utilities/TextSanitizer.cs`
- `Magnetar.Protocol` — `PlayerSnapshot`, `ServerCommandEnvelope`, `ServerCommandType`.
- MudBlazor — `MudTable`, `MudMenu`, `MudMenuItem`, `MudChip`, `MudDivider`, `ISnackbar`.

## Notes
- `ShouldRender()` override is a deliberate concurrency guard: the live `Changed` event fires on a background thread and could close an open `MudMenu` popup by forcing a re-render. Re-renders are suppressed while `_menuOpen` is true.
- Player names go through `TextSanitizer.CleanGameText` to strip control characters embedded in in-game names.
