# Quasar/Components/Dashboard/ServerDetailPanel.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Detail panel embedded inside `ServerCard`. When the agent snapshot is absent it shows a waiting/error message and basic process state chips. When a snapshot is present it renders live metrics chips, Refresh/Save buttons, a chat broadcast field, a players table with per-player action menus (kick/ban confirm first; set role sends directly), a recent-chat list, and recent command results. Server-authored chat (`IsServerMessage`, SteamId 0, `Good.bot`, or `Server`) is displayed as `Server`. In both the snapshot-present and snapshot-absent states, an outlined "Affinity <value>" chip (Memory icon) is shown in the metrics chip rows when `Server.CpuAffinity` is set, and mod-download failures captured from runtime output are shown as an explicit error alert.

## Structure
No `@page` route — used as a child component.

**Injected services:**
- `AgentRegistry Registry` — dispatches `ServerCommandEnvelope` messages to the agent.
- `QuasarConfigProfileCatalog ConfigProfiles` — resolves configured `MaxPlayers` from the linked config profile.
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
- `GetMaxPlayers()` — checks config profile first, falls back to snapshot metrics.
- `GetWaitingText()` — state-dependent placeholder message. For a `Running` process with no snapshot yet (agent reconnecting) it reads "Connecting. Waiting for Quasar.Agent to reconnect."; for `Starting`/`Restarting` it reads "Starting. Waiting for Quasar.Agent and first game snapshot.".
- Top-level runtime alert — when `Runtime.ModDownloadFailures` contains entries, shows "Mod download failed during world initialization" with recent captured failure lines.
- `GetPlatformName` / `GetRoleLabel` / `IsCurrentPromoteLevel` / `GetServiceLabel` — player table helpers.

**Static field:** `PromoteLevels = ["None", "Scripter", "Moderator", "SpaceMaster", "Admin"]`.

**MudBlazor components used:** `MudAlert`, `MudStack`, `MudChip`, `MudButton`, `MudTextField`, `MudTable`, `MudTh`, `MudTd`, `MudMenu`, `MudMenuItem`, `MudDivider`, `MudText`.

## Dependencies
- [`Quasar/Services/AgentRegistry.cs`](../../Services/AgentRegistry.cs.md) — command dispatch and agent lookup
- `Quasar/Services/QuasarConfigProfileCatalog.cs` — max-player resolution
- `Magnetar.Protocol.Model.ServerCommandEnvelope`, `ServerCommandType`
- `Magnetar.Protocol.Model.DedicatedServerDefinition`
- `Magnetar.Protocol.Model.DedicatedServerRuntimeSnapshot`, `DedicatedServerProcessState`
- `Magnetar.Protocol.Model.AgentRuntimeState`, `PlayerSnapshot`
- `Quasar.TextSanitizer` — game-text cleaning for player names and chat
- MudBlazor

## Notes
`ShouldRender()` returns `false` while `_menuOpen` is `true`, preventing the live-update cycle from collapsing an open player action menu. The parent Dashboard page re-renders this panel on every agent snapshot tick.
