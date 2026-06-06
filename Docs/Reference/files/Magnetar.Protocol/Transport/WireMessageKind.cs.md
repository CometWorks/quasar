# Magnetar.Protocol/Transport/WireMessageKind.cs

**Module:** Magnetar.Protocol  **Kind:** class (static)  **Tier:** 1

## Summary
String constants for the `AgentWireMessage.Kind` discriminator — the shared vocabulary of message types on the Quasar ↔ agent WebSocket channel. Both ends compare against these constants to route each envelope to the correct handler. Values travel on the wire, so they must stay stable across versions.

## Structure
Namespace `Magnetar.Protocol.Transport`; `public static class WireMessageKind`. All `public const string`:

| Constant | Value | Direction | Description |
|---|---|---|---|
| `Hello` | `"hello"` | Agent→Quasar | Handshake/identity after connect. |
| `Snapshot` | `"snapshot"` | Agent→Quasar | Periodic server state push. |
| `Command` | `"command"` | Quasar→Agent | Command request envelope. |
| `CommandResult` | `"command-result"` | Agent→Quasar | Command response. |
| `Ping` | `"ping"` | Either | Keep-alive ping. |
| `Pong` | `"pong"` | Either | Keep-alive pong reply. |
| `PluginConfigSnapshot` | `"plugin-config-snapshot"` | Agent→Quasar | Current plugin config state. |
| `PluginConfigUpdate` | `"plugin-config-update"` | Quasar→Agent | Apply updated plugin config values. |
| `AdminStop` | `"admin-stop"` | Agent→Quasar | Admin/console-initiated stop Quasar did not request. |
| `PluginLogs` | `"plugin-logs"` | Agent→Quasar | Batch of streamed plugin log lines. |

## Dependencies
- [`Magnetar.Protocol/Transport/AgentWireMessage.cs`](AgentWireMessage.cs.md) — `Kind` is set to one of these constants.

## Notes
`PluginLogs` is the newer kind backing the live plugin-log streaming channel (pairs with `AgentWireMessage.PluginLogs`); it replaces stdout capture for the log panel and tolerates Quasar restarts/reconnects. Renaming any value is a breaking protocol change.
