# Quasar/Components/Dashboard/ServerDetailPanel.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Detail panel embedded inside `ServerCard`. When the agent snapshot is absent it shows a waiting/error message and basic process state chips. When a snapshot is present it renders live metrics chips, Refresh/Save buttons, a chat broadcast field, a players table with player identity/status columns and a rightmost unlabeled action menu column, a recent-chat list, and recent command results. The Plugins chip compares loaded runtime plugins against the assigned config profile's selected plugins, displays `loaded/total`, and turns warning-colored when the loaded count differs from the configured total; if no profile is available it falls back to the aggregate agent plugin metric. The Save chip shows save-in-progress state, or the latest world-save local time plus unsaved in-game progress as `MM:SS`, with a tooltip containing the full local timestamp. Server-authored chat (`IsServerMessage`, SteamId 0, `Good.bot`, or `Server`) is displayed as `Server`. In both the snapshot-present and snapshot-absent states, an outlined "Affinity <value>" chip (Memory icon) is shown in the metrics chip rows when `Server.CpuAffinity` is set, and mod-download failures captured from runtime output are shown as an explicit error alert.

## Structure
No `@page` route — used as a child component.

**Injected services:**
- `AgentRegistry Registry` — dispatches `ServerCommandEnvelope` messages to the agent.
- `QuasarConfigProfileCatalog ConfigProfiles` — resolves configured `MaxPlayers` and selected plugins from the linked config profile.
- `ISnackbar Snackbar` — success/error toast notifications.
- `IDialogService DialogService` — confirms Kick/Ban player commands.

**Parameters:**
| Parameter | Type |
|---|---|
| `Server` | `DedicatedServerDefinition?` |
| `Runtime` | `DedicatedServerRuntimeSnapshot?` |
| `Agent` | `AgentRuntimeState?` |

**Key private state:**
- `_chatText` — bound to the broadcast message field.
- `_menuOpen` — suppresses re-render while a player action menu is open via `ShouldRender()`.

**Key private methods:**
- `SendCommandAsync(ServerCommandType, text, steamId?)` — confirms Kick/Ban player commands, builds and sends a `ServerCommandEnvelope` via `AgentRegistry`, shows a snackbar.
- `SendChatAsync()` — validates and dispatches `ServerCommandType.SendChat`.
- `HandleChatKeyDownAsync` — triggers send on Enter key.
- `FormatDuration(int)` / `FormatTimestamp(long)` / `FormatChatAuthor` — display helpers, including server-message author normalization.
- `GetSaveChipText()` / `GetSaveTooltipText()` / `GetSaveChipColor()` / `FormatMinuteSecondDuration(long)` — render the world-save chip from `ServerMetrics.IsSaveInProgress`, `LastWorldSaveUtc`, and `UnsavedGameTimeSeconds`.
- `GetMaxPlayers()` — checks config profile first, falls back to snapshot metrics.
- `GetPluginLoadSummary()` / `GetPluginLoadText()` / `GetPluginLoadColor()` — derive the Plugins chip from loaded runtime plugin IDs/display names against the assigned profile's selected plugin IDs/display names, and mark mismatches with `Color.Warning`.
- `GetWaitingText()` — state-dependent placeholder message. For a `Running` process with no snapshot yet (agent reconnecting) it reads "Connecting. Waiting for Quasar.Agent to reconnect."; for `Starting`/`Restarting` it reads "Starting. Waiting for Quasar.Agent and first game snapshot.".
- Top-level runtime alert — when `Runtime.ModDownloadFailures` contains entries, shows "Mod download failed during world initialization" with recent captured failure lines.
- `GetPlatformName` / `GetRoleLabel` / `IsCurrentPromoteLevel` / `GetServiceLabel` — player table helpers.

**Static field:** `PromoteLevels = ["None", "Scripter", "Moderator", "SpaceMaster", "Admin"]`.

**MudBlazor components used:** `MudAlert`, `MudStack`, `MudChip`, `MudTooltip`, `MudButton`, `MudTextField`, `MudTable`, `MudTh`, `MudTd`, `MudMenu`, `MudMenuItem`, `MudDivider`, `MudText`.

## Dependencies
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md) — command dispatch and agent lookup
- `Quasar/Services/QuasarConfigProfileCatalog.cs` — config-profile max-player and selected-plugin resolution
- [`Quasar/Models/QuasarConfigProfile.cs`](../../Models/QuasarConfigProfile.cs.md) — selected plugin profile data
- `Magnetar.Protocol.Model.ServerCommandEnvelope`, `ServerCommandType`
- `Magnetar.Protocol.Model.DedicatedServerDefinition`
- `Magnetar.Protocol.Model.DedicatedServerRuntimeSnapshot`, `DedicatedServerProcessState`
- `Magnetar.Protocol.Model.AgentRuntimeState`, `PlayerSnapshot`, `PluginRuntimeInfo`
- `Quasar.TextSanitizer` — game-text cleaning for player names and chat
- MudBlazor

## Notes
`ShouldRender()` returns `false` while `_menuOpen` is `true`, preventing the live-update cycle from collapsing an open player action menu. The parent Dashboard page re-renders this panel on every agent snapshot tick.
