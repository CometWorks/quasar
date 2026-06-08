# Quasar/Components/Dashboard/ServerCard.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Card component for a single managed server shown on the Dashboard. Displays the server display name, status chip (OFF / STARTING / CONNECTING / OPEN / STOPPING), host/world caption, last message or health summary, Start / Stop / Restart action buttons, and a terminal icon button for opening the server log dialog beside Restart. Embeds `ServerDetailPanel` as its card body content.

## Structure
No `@page` route — used as a child component.

**Parameters:**
| Parameter | Type | Notes |
|---|---|---|
| `Server` | `DedicatedServerDefinition` | Required. Static server config. |
| `Runtime` | `DedicatedServerRuntimeSnapshot?` | Live process state snapshot. |
| `Agent` | `AgentRuntimeState?` | Live agent/game state. |
| `StartRequested` | `EventCallback<string>` | Fires with `UniqueName` when Start clicked. |
| `StopRequested` | `EventCallback<string>` | Fires with `UniqueName` when Stop clicked. |
| `RestartRequested` | `EventCallback<string>` | Fires with `UniqueName` when Restart clicked. |
| `OpenLogsRequested` | `EventCallback<string>` | Fires with `UniqueName` when the terminal/log button is clicked. |

**Key MudBlazor components:** `MudCard`, `MudCardHeader`, `MudCardContent`, `MudStack`, `MudChip`, `MudButton`, `MudIconButton`, `MudTooltip`, `MudText`.

**Private helpers:**
- `ProcessState` — derives `DedicatedServerProcessState` from `Runtime?.State`.
- `IsProcessActive`, `CanStart`, `CanStop`, `CanRestart` — button visibility logic. Restart is shown for every active process state (`Starting`, `Running`, `Restarting`, `Stopping`), not only the fully running state.
- `GetDisplayName()` — prefers `Server.DisplayName`, falls back to `Agent.ServerDisplayName`, then `UniqueName`.
- `GetHostLabel()` — shows `Agent.HostDisplayName` or "Local host".
- `GetWorldLabel()` — shows `Agent.WorldDisplayName`, else last path segment of `Server.WorldPath`, else "World pending".
- `GetStatusLabel()` / `GetStatusColor()` — status chip text and `Color` enum. Shows `CONNECTING` (Info) when the process is `Running` but the agent link is not yet established — e.g. just after a Quasar restart that adopted a running process, or a transient agent drop — versus `STARTING` (Warning) for a genuine boot.

## Dependencies
- [`Quasar/Components/Dashboard/ServerDetailPanel.razor`](ServerDetailPanel.razor.md) — embedded in card body
- `Magnetar.Protocol.Model.DedicatedServerDefinition` — static server config type
- `Magnetar.Protocol.Model.DedicatedServerRuntimeSnapshot` — runtime state parameter
- `Magnetar.Protocol.Model.DedicatedServerProcessState` — process state enum
- `Magnetar.Protocol.Model.AgentRuntimeState` — agent connection state parameter
- MudBlazor
