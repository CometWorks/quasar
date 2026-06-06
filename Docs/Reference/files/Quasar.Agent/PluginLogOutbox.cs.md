# Quasar.Agent/PluginLogOutbox.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
Bounded, thread-safe buffer that captures plugin log lines emitted in-process by the PluginSdk Quasar log sink and hands them to `AgentConnection` in batches for streaming to Quasar (as `PluginLogBatch` / `WireMessageKind.PluginLogs`). The buffer survives Quasar outages: lines accumulate while disconnected and are flushed on reconnect, so the supervisor's "Recent plugin logs" panel is backfilled rather than losing everything captured while Quasar was down.

## Structure
**Namespace:** `Quasar.Agent`  **Modifiers:** `public sealed`, implements `IDisposable`

Constants: `MaxBufferedLines = 10000`, `MaxBatchLines = 500`. Backed by a `ConcurrentQueue<string>` with an interlocked `_count` and `_subscribed` flag.

| Member | Description |
|---|---|
| `Start()` | Idempotent (interlocked); subscribes `LogEnvironment.LineEmitted` → `Enqueue`. |
| `Dispose()` | Idempotent; unsubscribes the event handler. |
| `Enqueue(line)` (private) | Skips null/empty; enqueues; if over `MaxBufferedLines`, drops the oldest line first. |
| `DrainBatch()` | Removes and returns up to `MaxBatchLines` lines (oldest first); empty list when nothing queued. |
| `Requeue(lines)` | Returns lines to the buffer after a failed send (via `Enqueue`); order not preserved. |

## Dependencies
- [`Quasar.Agent/AgentConnection.cs`](AgentConnection.cs.md) — owner that drains/requeues and sends batches.
- [`Magnetar.Protocol/Model/PluginLogBatch.cs`](../Magnetar.Protocol/Model/PluginLogBatch.cs.md) — wire DTO carrying drained lines.
- `PluginSdk.Logging` — `LogEnvironment.LineEmitted` (source of formatted sink lines).

## Notes
NEW file. The cap bounds memory under a long outage or a chatty plugin and keeps a single wire message a reasonable size; when full it drops oldest-first, matching Quasar's own per-server ring-buffer policy. `Requeue` does not preserve ordering, which is acceptable because each line embeds its own timestamp and the panel sorts by it. All counters use `Interlocked`, so capture (game/plugin threads) and draining (connection task) are safe to run concurrently.
