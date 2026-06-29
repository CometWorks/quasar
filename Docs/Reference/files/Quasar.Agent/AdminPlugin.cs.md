# Quasar.Agent/AdminPlugin.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
`AdminPlugin` is the Magnetar `IPlugin` entry point for the Quasar agent that runs inside the Space Engineers dedicated server. On `Init` it reads `AgentOptions`, configures and applies profiler Harmony patches plus status-emissive capture patches, builds the `GameBridge`, starts a `PluginLogOutbox` (begun before the connection so startup log lines are buffered), and starts an `AgentConnection`. It drives the game-thread snapshot/profiler refresh on each `Update`, and handles two lifetime events: player death (forwarded as a `DeathEventSnapshot`) and server termination (sends an `AdminStop` signal to Quasar when the shutdown was admin-initiated, not Quasar-requested).

## Structure
**Namespace:** `Quasar.Agent`  **Base:** `IPlugin` (VRage.Plugins)  **Modifiers:** public, concrete

Fields: `_bridge` (`GameBridge`), `_connection` (`AgentConnection`), `_outbox` (`PluginLogOutbox`).

| Member | Description |
|---|---|
| `Init(object gameServer)` | Reads `AgentOptions.FromEnvironment()`, calls `AgentProfiler.Configure(options)`, `AgentProfilerPatches.Apply(options)`, and `EmissivePartCapturePatches.Apply()`, builds `GameBridge`; creates and `Start()`s `PluginLogOutbox` (before the connection loop); constructs `AgentConnection(bridge, WebServiceLocator, options, outbox)` and starts it; subscribes `MyVisualScriptLogicProvider.PlayerDied` and `ServerControl.Terminating`. |
| `Update()` | Delegates to `GameBridge.Update()` each game tick. |
| `Dispose()` | Unsubscribes events, stops the connection, disposes the outbox, unpatches emissive-capture and profiler hooks, nulls references. |
| `OnServerTerminating(ServerTerminationKind kind)` | If `kind == Shutdown` and `!_bridge.QuasarRequestedStop`, calls `AgentConnection.TrySendAdminStop()`. |
| `OnPlayerDied(long identityId)` | Resolves victim display name via `MySession.Static.Players`, records a `DeathEventSnapshot` (`DeathType = "Accident"`) via `GameBridge.RecordDeath`. |

## Dependencies
- `Quasar.Agent/GameBridge.cs`
- [`Quasar.Agent/AgentProfilerPatches.cs`](AgentProfilerPatches.cs.md)
- [`Quasar.Agent/AgentProfiler.cs`](AgentProfiler.cs.md)
- [`Quasar.Agent/EmissivePartCapturePatches.cs`](EmissivePartCapturePatches.cs.md)
- [`Quasar.Agent/AgentConnection.cs`](AgentConnection.cs.md)
- [`Quasar.Agent/PluginLogOutbox.cs`](PluginLogOutbox.cs.md)
- `Quasar.Agent/WebServiceLocator.cs`
- `Quasar.Agent/AgentOptions.cs`
- `Magnetar.Protocol/Model/DeathEventSnapshot.cs`
- `PluginSdk` — `IPlugin`, `ServerControl.Terminating`, `ServerTerminationKind`
- `Sandbox.Game` — `MyVisualScriptLogicProvider.PlayerDied`; `Sandbox.Game.World` — `MySession`
- `VRage.Plugins` — `IPlugin`

## Notes
The `PluginLogOutbox` is created and started before the `AgentConnection` so plugin log lines emitted during startup are buffered and shipped once connected. `OnServerTerminating` only acts on `Shutdown`; restarts are left alone so the server can come back. The `!_bridge.QuasarRequestedStop` guard avoids a redundant admin-stop notification when Quasar itself ordered the shutdown.
