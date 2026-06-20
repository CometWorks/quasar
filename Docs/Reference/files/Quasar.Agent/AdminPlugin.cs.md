# Quasar.Agent/AdminPlugin.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
`AdminPlugin` is the Magnetar `IPlugin` entry point for the Quasar agent that runs inside the Space Engineers dedicated server. On `Init` it reads `AgentOptions`, configures and applies profiler Harmony patches, registers Quasar's admin chat commands (including the root `!stop` override), builds the `GameBridge`, starts a `PluginLogOutbox` (begun before the connection so startup log lines are buffered), wires `StopCommand` to report an admin stop before `!stop` quits the server, and starts an `AgentConnection`. It drives the game-thread snapshot/profiler refresh on each `Update`, refreshes per-character death subscriptions so respawned players are re-hooked, and handles server termination by sending an `AdminStop` signal to Quasar when shutdown was admin-initiated.

## Structure
**Namespace:** `Quasar.Agent`  **Base:** `IPlugin` (VRage.Plugins)  **Modifiers:** public, concrete

Fields: `_bridge` (`GameBridge`), `_connection` (`AgentConnection`), `_outbox` (`PluginLogOutbox`), `_adminStopSync`, `_adminStopReported`, `_deathSubscriptionsByIdentityId`, `_recentDeathsByIdentityId`.

| Member | Description |
|---|---|
| `Init(object gameServer)` | Reads `AgentOptions.FromEnvironment()`, calls `AgentProfiler.Configure(options)` and `AgentProfilerPatches.Apply(options)`, registers `StopCommand` through PluginSdk `ServerCommands`, builds `GameBridge`; creates and `Start()`s `PluginLogOutbox` (before the connection loop); constructs `AgentConnection(bridge, WebServiceLocator, options, outbox)`, assigns `StopCommand.AdminStopRequested = ReportAdminStop`, and starts the connection; subscribes `MyVisualScriptLogicProvider.PlayerDied` as a fallback and `ServerControl.Terminating`. |
| `Update()` | Delegates to `GameBridge.Update()` each game tick, then periodically scans online human players to hook their current `IMyCharacter.CharacterDied` event. |
| `Dispose()` | Unsubscribes process/session events and character death handlers, clears the `StopCommand` hook, stops the connection, disposes the outbox, unpatches profiler hooks, nulls references. |
| `OnServerTerminating(ServerTerminationKind kind)` | If `kind == Shutdown` and `!_bridge.QuasarRequestedStop`, calls `ReportAdminStop()` as a fallback for admin/console shutdowns outside the `!stop` command. |
| `ReportAdminStop()` | Locks `_adminStopSync` and sends `AgentConnection.TrySendAdminStop()` until one attempt succeeds, so `!stop` can report early while the termination fallback can retry if the socket was not open yet. |
| `RefreshDeathSubscriptions()` | Once per second, scans `MySession.Static.Players.GetOnlinePlayers()`, skips bots/NPC identities, and hooks each player's current character. |
| `HookCharacterDeath(IMyCharacter, long, string)` | Replaces stale per-identity character subscriptions when the character entity changes after respawn. |
| `OnCharacterDied(IMyCharacter, long, string)` / `OnPlayerDied(long)` | Resolve the victim name and record a `DeathEventSnapshot` (`DeathType = "Accident"`) via `GameBridge.RecordDeath`; duplicate character/visual-script notifications are suppressed per identity for a short window. |

## Dependencies
- `Quasar.Agent/GameBridge.cs`
- [`Quasar.Agent/StopCommand.cs`](StopCommand.cs.md)
- [`Quasar.Agent/AgentProfilerPatches.cs`](AgentProfilerPatches.cs.md)
- [`Quasar.Agent/AgentProfiler.cs`](AgentProfiler.cs.md)
- [`Quasar.Agent/AgentConnection.cs`](AgentConnection.cs.md)
- [`Quasar.Agent/PluginLogOutbox.cs`](PluginLogOutbox.cs.md)
- `Quasar.Agent/WebServiceLocator.cs`
- `Quasar.Agent/AgentOptions.cs`
- `Magnetar.Protocol/Model/DeathEventSnapshot.cs`
- `PluginSdk` — `IPlugin`, `ServerControl.Terminating`, `ServerTerminationKind`; `PluginSdk.Commands` — `ServerCommands`
- `Sandbox.Game` — `MyVisualScriptLogicProvider.PlayerDied`; `Sandbox.Game.World` — `MySession`; `VRage.Game.ModAPI` — `IMyCharacter.CharacterDied`
- `VRage.Plugins` — `IPlugin`

## Notes
The `PluginLogOutbox` is created and started before the `AgentConnection` so plugin log lines emitted during startup are buffered and shipped once connected. Death capture uses the same reliable shape as Torch-style player tracking: hook the live character, then re-hook when respawn gives the player a new character entity. `MyVisualScriptLogicProvider.PlayerDied` remains subscribed only as a fallback. `OnServerTerminating` only acts on `Shutdown`; restarts are left alone so the server can come back. `ReportAdminStop` is shared by `!stop` and the termination fallback so Quasar sees the shutdown intent early while still preserving coverage for other admin-triggered stops; it only suppresses later reports after `TrySendAdminStop()` succeeds.
