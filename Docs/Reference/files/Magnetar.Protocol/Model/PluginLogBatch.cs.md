# Magnetar.Protocol/Model/PluginLogBatch.cs

**Module:** Magnetar.Protocol  **Kind:** class (DTO)  **Tier:** 1

## Summary
DTO carrying a batch of plugin log lines the agent ships to Quasar over its WebSocket channel (as `AgentWireMessage.PluginLogs`, kind `WireMessageKind.PluginLogs`). Each entry is one formatted JSON line exactly as the PluginSdk `QuasarLogSink` renders it, so Quasar reuses its existing sink-line parser to turn them back into log entries. This channel replaces stdout capture for the live log panel: it keeps flowing after Quasar restarts and reconnects to a detached daemon, and the agent buffers lines while Quasar is unreachable so they are backfilled on reconnect.

## Structure
Namespace `Magnetar.Protocol.Model`; `public class PluginLogBatch`.

- `List<string> Lines` — ordered formatted sink lines; default empty list.

## Dependencies
- [`Magnetar.Protocol/Transport/AgentWireMessage.cs`](../Transport/AgentWireMessage.cs.md) (transports this DTO)

## Notes
NEW file. Lines are pre-rendered strings, not structured entries; ordering is best-effort since the agent's outbox may requeue a failed batch out of order, but each line embeds its own timestamp for the panel to sort by.
