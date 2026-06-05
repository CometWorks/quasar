# Quasar.Agent/AgentConnection.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
`AgentConnection` manages the raw WebSocket connection from the in-game agent to the Quasar supervisor. It runs a reconnect loop on a background task, sends a `Hello` handshake and periodic `Snapshot` messages, receives and dispatches `Command` / `PluginConfigUpdate` / `Ping` messages from Quasar, and implements autonomous save-and-stop if Quasar has been unreachable beyond a configurable window.

## Structure
**Namespace:** `Quasar.Agent`  
**Modifiers:** public, concrete

| Member | Description |
|---|---|
| `AgentConnection(GameBridge, WebServiceLocator, AgentOptions)` | Constructor; stores dependencies |
| `Start()` | Spawns background `RunAsync` task with a new `CancellationTokenSource` |
| `Stop()` | Cancels the task and waits up to 5 s for clean shutdown |
| `TrySendAdminStop()` | Best-effort synchronous fire-and-forget of an `AdminStop` wire message; blocks at most 2 s |
| `RunAsync` (private) | Main reconnect loop: locates service, connects WebSocket, sends Hello + plugin configs, runs snapshot and receive loops concurrently |
| `HandleDisconnectedAndDelayAsync` (private) | Tracks outage duration; triggers `ServerControl.SaveAndQuit()` once the configured offline window expires (or immediately if `OfflineShutdownSeconds <= 0`) |
| `SnapshotLoopAsync` (private) | Sends a `Snapshot` + plugin config diff every 2 s |
| `ReceiveLoopAsync` (private) | Deserializes incoming messages; dispatches `Command` to `GameBridge.ExecuteCommandAsync`, `PluginConfigUpdate` to `GameBridge.ApplyPluginConfigAsync`, and `Ping` with a `Pong` |
| `SendPluginConfigsAsync` (private) | Compares serialized plugin config to last sent; only sends when changed or `force=true` |
| `SendAsync` (private) | Serializes `AgentWireMessage` to JSON, sends over WebSocket under `_sendLock` |
| `ReceiveAsync` (private, static) | Reassembles fragmented WebSocket text frames into a full `AgentWireMessage` |

**WebSocket sub-protocol:** `quasar.agent.v1`  
**Keep-alive interval:** 20 s

## Dependencies
- [`Quasar.Agent/GameBridge.cs`](GameBridge.cs.md)
- [`Quasar.Agent/WebServiceLocator.cs`](WebServiceLocator.cs.md)
- [`Quasar.Agent/AgentOptions.cs`](AgentOptions.cs.md)
- `Magnetar.Protocol.Model` — `AgentWireMessage`, `WireMessageKind`, `PluginConfigSnapshot`
- `Magnetar.Protocol.Transport` — wire transport types
- `PluginSdk` — `ServerControl.SaveAndQuit()`
- `Newtonsoft.Json` — serialization with camelCase + null-ignore settings

## Notes
- Send path is protected by `SemaphoreSlim(1,1)` (`_sendLock`) to prevent concurrent WebSocket writes.
- The `_socket` field is `volatile` so `TrySendAdminStop` can read it safely from the game thread without a lock.
- The autonomous self-stop only arms after at least one successful connection (`_hasConnected`), so a server that never reached Quasar is never auto-stopped.
- Reconnect delay uses jitter (`±ReconnectJitterSeconds`) to spread reconnect storms.
