# Quasar.Agent/AgentConnection.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
`AgentConnection` manages the WebSocket connection from the in-DS agent to the Quasar supervisor. It runs a reconnect loop on a background task, sends a `Hello` handshake plus periodic `Snapshot` messages, streams buffered plugin log batches from a `PluginLogOutbox`, receives and dispatches `Command` / `PluginConfigUpdate` / `Ping` messages from Quasar, and performs an autonomous save-and-stop if Quasar stays unreachable past a configurable window.

## Structure
**Namespace:** `Quasar.Agent`  **Modifiers:** public, concrete

Notable fields: `_bridge`, `_locator`, `_options`, `_outbox` (`PluginLogOutbox`), `_sendLock` (`SemaphoreSlim(1,1)`), `volatile ClientWebSocket _socket`, `_lastPluginConfigJson`, `_hasConnected`, `_disconnectedSinceUtc`. Static `JsonSettings` (camelCase, null-ignore).

| Member | Description |
|---|---|
| `AgentConnection(GameBridge, WebServiceLocator, AgentOptions, PluginLogOutbox)` | Stores dependencies (outbox now injected). |
| `Start()` / `Stop()` | Spawn / cancel-and-join (5 s) the background `RunAsync` loop. |
| `TrySendAdminStop()` | Best-effort synchronous `AdminStop` send (≤2 s) while the socket is still open; reads `_socket` (volatile) from the game thread and returns whether the signal was sent before timeout/failure. |
| `RunAsync` (private) | Reconnect loop: locate service, connect (`wss`/`ws`, sub-protocol `quasar.agent.v1`, 20 s keep-alive), send `Hello`, force-send plugin configs, run snapshot + receive loops concurrently. |
| `HandleDisconnectedAndDelayAsync` (private) | Tracks outage; once armed (`_hasConnected`) and past the window, calls `ServerControl.SaveAndQuit()`; else waits a jittered delay. |
| `ShouldSelfStop` / `NextReconnectDelay` (private) | Offline-window check (`<=0` means stop promptly); jittered reconnect interval (≥1 s). |
| `SnapshotLoopAsync` (private) | Every 2 s: send `Snapshot`, send changed plugin configs, then `FlushPluginLogsAsync`. |
| `FlushPluginLogsAsync` (private) | Drains the outbox in capped batches and sends each as `PluginLogs` (`PluginLogBatch`); on send failure requeues the batch and rethrows so the connection re-establishes. |
| `ReceiveLoopAsync` (private) | Dispatches `Command`→`GameBridge.ExecuteCommandAsync` (+`CommandResult`), `PluginConfigUpdate`→`GameBridge.ApplyPluginConfigAsync` (then force re-sends configs), `Ping`→`Pong`. |
| `SendPluginConfigsAsync` (private) | Sends `PluginConfigSnapshot` only when serialized state changed or `force=true`. |
| `SendAsync` / `ReceiveAsync` (private) | Serialize+send under `_sendLock`; reassemble fragmented text frames into an `AgentWireMessage`. |

## Dependencies
- `Quasar.Agent/GameBridge.cs`
- `Quasar.Agent/WebServiceLocator.cs`
- `Quasar.Agent/AgentOptions.cs`
- [`Quasar.Agent/PluginLogOutbox.cs`](PluginLogOutbox.cs.md)
- [`Magnetar.Protocol/Transport/AgentWireMessage.cs`](../Magnetar.Protocol/Transport/AgentWireMessage.cs.md), [`Magnetar.Protocol/Transport/WireMessageKind.cs`](../Magnetar.Protocol/Transport/WireMessageKind.cs.md)
- `Magnetar.Protocol/Model/PluginConfigSnapshot.cs`, [`Magnetar.Protocol/Model/PluginLogBatch.cs`](../Magnetar.Protocol/Model/PluginLogBatch.cs.md)
- `PluginSdk` — `ServerControl.SaveAndQuit()`
- `Newtonsoft.Json` — camelCase + null-ignore serialization

## Notes
- Sends are serialized via `_sendLock` to prevent concurrent WebSocket writes (snapshot loop, log flush, and `TrySendAdminStop` can all send). `TrySendAdminStop()` returns `false` when the socket is missing, closed, times out, or throws so `AdminPlugin` can leave the termination fallback eligible to retry.
- Plugin-log streaming: `FlushPluginLogsAsync` flushes a backlog promptly on reconnect in `MaxBatchLines`-sized chunks; a failed batch is returned to the outbox so no lines are lost.
- The autonomous self-stop only arms after at least one successful connection (`_hasConnected`), so a server that never reached Quasar is never auto-stopped. Reconnect uses jitter to spread reconnect storms.
