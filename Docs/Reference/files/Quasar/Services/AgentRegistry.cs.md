# Quasar/Services/AgentRegistry.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

Thread-safe, in-memory registry of all connected Quasar.Agent instances. It tracks connection state (hello, snapshot, profiler timing, command results) for every agent WebSocket session, routes outbound commands via per-agent sender delegates, and surfaces observable runtime state through a `Changed` event. It is the canonical source of live agent data consumed by the supervisor and UI. `AgentRuntimeState` is the companion per-agent mutable state bag.

## Structure

Namespace: `Quasar.Services`

**`AgentRegistry`** — sealed class, singleton (DI).

| Member | Description |
|---|---|
| `event Action? Changed` | Fires after any state mutation; drives UI refresh. |
| `GetAgents()` | Cloned, host-then-server sorted snapshot of all agents. |
| `PruneDisconnectedByUniqueName(uniqueName)` | Removes stale disconnected entries for a server unique name. |
| `UpsertHello(hello, connectionId, sender)` | Registers or updates agent on handshake; stores the async sender delegate. |
| `UpdateSnapshot(snapshot, connectionId)` | Merges an `AgentSnapshot`; forwards player state to `KnownPlayerCatalog`, scalar samples to `MetricsStoreService`, and optional profiler snapshots to `ProfilerStoreService`. |
| `UpdateCommandResult(result)` | Stores result in ring buffer (max 20), resolves any waiting `TaskCompletionSource`, updates player catalog. |
| `MarkDisconnected(connectionId)` | Marks agents as disconnected; faults all pending `SendCommandAndWaitAsync` awaiters. |
| `SendCommandAsync(command, ct)` | Fire-and-forget command dispatch. |
| `SendToAgentAsync(agentId, message, ct)` | Fire-and-forget arbitrary `AgentWireMessage` (e.g. plugin config push). |
| `SendCommandAndWaitAsync(command, timeout, ct)` | Request/response: sends command and awaits matching `ServerCommandResult`; throws `TimeoutException` or `InvalidOperationException`. |
| `TryGetUniqueName(connectionId, out uniqueName)` | Looks up server unique name for a socket connection. |

**`AgentRuntimeState`** — sealed class; companion mutable per-agent bag.

Properties: `AgentId`, `ConnectionId`, `IsConnected`, `LastSeenUtc`, `Hello`, `Snapshot`, `CommandResults`, `Sender`. Computed: `UniqueNameKey`, `HostKey`, `ServerKey`, `HostDisplayName`, `ServerDisplayName`, `WorldDisplayName`. Has `Clone()`.

## Dependencies

- [`Quasar/Services/KnownPlayerCatalog.cs`](KnownPlayerCatalog.cs.md) — `ObserveSnapshot`, `ApplyCommandOutcome`
- [`Quasar/Services/Analytics/MetricsStoreService.cs`](Analytics/MetricsStoreService.cs.md) — `Enqueue` on each snapshot
- [`Quasar/Services/Analytics/ProfilerStoreService.cs`](Analytics/ProfilerStoreService.cs.md) — stores optional profiler snapshots from agent telemetry
- `Magnetar.Protocol.Model` — `AgentHello`, `AgentSnapshot`, `PlayerSnapshot`
- `Magnetar.Protocol.Transport` — `AgentWireMessage`, `ServerCommandEnvelope`, `ServerCommandResult`, `WireMessageKind`

## Notes

All mutations guarded by `_sync`. The sender delegate is stored inside `AgentRuntimeState`; the lock is released before awaiting I/O. Pending awaiters (`_pendingResults` keyed by `CommandId`) are completed or faulted outside the lock. `SendCommandAndWaitAsync` timeout uses a linked `CancellationTokenSource`; the cancellation callback sets either `TrySetCanceled` or `TrySetException(TimeoutException)` depending on which token fired.
