# Magnetar.Protocol/Transport/WireMessageKind.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Static class of string constants used as the `Kind` discriminator in `AgentWireMessage`. These string values are transmitted on the wire, so they must remain stable across versions.

## Structure
Namespace: `Magnetar.Protocol.Transport`

Class `WireMessageKind` (static):

| Constant | Value | Direction | Description |
|---|---|---|---|
| `Hello` | `"hello"` | Agent→Supervisor | Initial handshake after WebSocket connect. |
| `Snapshot` | `"snapshot"` | Agent→Supervisor | Periodic full-state snapshot push. |
| `Command` | `"command"` | Supervisor→Agent | Command request envelope. |
| `CommandResult` | `"command-result"` | Agent→Supervisor | Command response. |
| `Ping` | `"ping"` | Either | Keepalive ping. |
| `Pong` | `"pong"` | Either | Keepalive pong reply. |
| `PluginConfigSnapshot` | `"plugin-config-snapshot"` | Agent→Supervisor | Full plugin configuration push. |
| `PluginConfigUpdate` | `"plugin-config-update"` | Supervisor→Agent | Apply updated plugin config values. |
| `AdminStop` | `"admin-stop"` | Supervisor→Agent | Signal agent to shut down gracefully. |

## Dependencies
- [`Magnetar.Protocol/Transport/AgentWireMessage.cs`](AgentWireMessage.cs.md) — `Kind` field is set to one of these constants.

## Notes
Values are wire-stable strings; renaming them is a breaking protocol change.
