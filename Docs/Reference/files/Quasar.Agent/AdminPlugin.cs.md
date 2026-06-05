# Quasar.Agent/AdminPlugin.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
`AdminPlugin` is the Magnetar `IPlugin` entry point for the Quasar agent. It wires together `GameBridge`, `AgentConnection`, and `WebServiceLocator` on `Init`, drives the game-thread snapshot refresh on each `Update` tick, and handles the two server-lifetime events: player death (forwarded as a `DeathEventSnapshot`) and server termination (sends an `AdminStop` signal to Quasar when the shutdown was admin-initiated rather than Quasar-requested).

## Structure
**Namespace:** `Quasar.Agent`  
**Base:** `IPlugin` (VRage.Plugins)  
**Modifiers:** public, concrete

| Member | Description |
|---|---|
| `Init(object gameServer)` | Creates `GameBridge`, `AgentConnection`, starts the connection loop, subscribes to `PlayerDied` and `ServerControl.Terminating` events |
| `Update()` | Delegates to `GameBridge.Update()` each game tick for snapshot refresh |
| `Dispose()` | Unsubscribes events, stops the connection, nulls references |
| `OnServerTerminating(ServerTerminationKind kind)` | If kind is `Shutdown` and `!_bridge.QuasarRequestedStop`, calls `AgentConnection.TrySendAdminStop()` |
| `OnPlayerDied(long identityId)` | Resolves victim display name via `MySession.Static.Players`, enqueues a `DeathEventSnapshot` via `GameBridge.RecordDeath` |

## Dependencies
- [`Quasar.Agent/GameBridge.cs`](GameBridge.cs.md)
- [`Quasar.Agent/AgentConnection.cs`](AgentConnection.cs.md)
- [`Quasar.Agent/WebServiceLocator.cs`](WebServiceLocator.cs.md)
- [`Quasar.Agent/AgentOptions.cs`](AgentOptions.cs.md)
- `Magnetar.Protocol.Model` — `DeathEventSnapshot`
- `PluginSdk` — `IPlugin`, `ServerControl.Terminating`, `ServerTerminationKind`
- `Sandbox.Game` — `MyVisualScriptLogicProvider.PlayerDied`
- `Sandbox.Game.World` — `MySession`
- `VRage.Plugins` — `IPlugin`

## Notes
`OnServerTerminating` only fires on `Shutdown`; restarts are intentionally left unhandled so Quasar can restart the server as normal. The check `!_bridge.QuasarRequestedStop` prevents a double-stop notification when Quasar itself issued the shutdown command.
