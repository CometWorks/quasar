# Magnetar.Protocol/Model/AgentHello.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Handshake payload sent by `Quasar.Agent` to the Quasar supervisor immediately after the WebSocket connection is established. Carries all static identity information needed by the supervisor to register the agent connection.

## Structure
Namespace: `Magnetar.Protocol.Model`

Class `AgentHello` (concrete, no base type):

| Property | Type | Description |
|---|---|---|
| `UniqueName` | `string` | Human-readable unique name of the SE server (matches the supervisor's `uniqueName`). |
| `AgentId` | `string` | Runtime-assigned GUID for this agent connection. |
| `HostId` | `string` | Identifier of the hosting machine. |
| `HostName` | `string` | Human-readable host name. |
| `ServerId` | `string` | Persistent identifier for the SE dedicated server. |
| `ServerName` | `string` | Display name of the SE dedicated server. |
| `WorldName` | `string` | Active world name. |
| `PluginId` | `string` | Plugin identifier of `Quasar.Agent` (version-independent). |
| `PluginVersion` | `string` | Semver string of the loaded agent plugin. |
| `ProcessId` | `int` | PID of the DS process. |
| `ProcessName` | `string` | Process image name. |
| `GameVersion` | `string` | Space Engineers game version string. |
| `ConnectedAtUtc` | `DateTimeOffset` | UTC timestamp of connection (defaults to `UtcNow`). |

## Dependencies
- Carried inside [`Magnetar.Protocol/Transport/AgentWireMessage.cs`](../Transport/AgentWireMessage.cs.md) as the `Hello` field.

## Notes
Transmitted as a `WireMessageKind.Hello` message. The supervisor uses `AgentId` + `UniqueName` to uniquely identify the connection for subsequent routing.
