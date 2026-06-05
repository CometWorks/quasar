# Quasar/Services/AgentSocketHandler.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`AgentSocketHandler` is the HTTP/WebSocket entry point for incoming Quasar.Agent connections. It accepts the `quasar.agent.v1` sub-protocol, drives the per-connection read loop, dispatches each `AgentWireMessage` to the appropriate service, and cleans up the registry on disconnect. It is the bridge between the raw WebSocket transport and the `AgentRegistry`.

## Structure

Namespace: `Quasar.Services`

**`AgentSocketHandler`** — sealed class.

| Member | Description |
|---|---|
| `HandleAsync(HttpContext)` | Upgrades request to WebSocket, assigns GUID connection ID, runs read loop, calls `_registry.MarkDisconnected` in `finally`. |
| `ProcessMessageAsync(message, connectionId, socket, ct)` | Dispatches on `WireMessageKind`: `Hello` → `UpsertHello`; `Snapshot` → `UpdateSnapshot`; `CommandResult` → `UpdateCommandResult`; `PluginConfigSnapshot` → `_pluginConfigService.IngestSnapshot`; `AdminStop` → `_supervisor.SetGoalStateAsync(Off)` using `_lifetime.ApplicationStopping`; `Ping` → `Pong` reply. |
| `ReceiveAsync(WebSocket, ct)` | Reads fragmented text frames into a `MemoryStream`, deserialises as `AgentWireMessage` via `System.Text.Json`. |
| `SendAsync(WebSocket, AgentWireMessage, ct)` | Serialises and sends as a single text frame. |

JSON options: `JsonSerializerDefaults.Web`, `WhenWritingNull`.

## Dependencies

- [`Quasar/Services/AgentRegistry.cs`](AgentRegistry.cs.md) — primary target of all dispatched messages
- [`Quasar/Services/DedicatedServerSupervisor.cs`](DedicatedServerSupervisor.cs.md) — `SetGoalStateAsync`
- `Quasar/Services/PluginSdk/PluginConfigService.cs` — `IngestSnapshot`
- `Magnetar.Protocol.Transport` — `AgentWireMessage`, `WireMessageKind`
- ASP.NET Core — `HttpContext`, `IHostApplicationLifetime`, `WebSocket`

## Notes

Critical cancellation design: state-mutating handlers (`AdminStop`) use `_lifetime.ApplicationStopping` rather than `context.RequestAborted`. The agent closes its socket the instant it sends the stop signal (its process is exiting), which would cancel the request token mid-flight and silently drop the goal-state write. Using the application-lifetime token ensures the mutation completes regardless of socket closure. This is documented inline in the source.
