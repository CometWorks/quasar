# Magnetar.Protocol/Model/PluginConfigSnapshot.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Container DTO grouping all configurable plugins reported by a single agent in one wire message. Keyed by `AgentId` on the Quasar side so editors can be matched to the originating server connection.

## Structure
Namespace: `Magnetar.Protocol.Model`

Class `PluginConfigSnapshot` (concrete, no base type):

| Property | Type | Description |
|---|---|---|
| `AgentId` | `string` | Runtime GUID of the agent that sent this snapshot. |
| `Plugins` | `List<PluginConfigData>` | One entry per plugin that implements `IQuasarConfigProvider`. |

## Dependencies
- [`Magnetar.Protocol/Model/PluginConfigData.cs`](PluginConfigData.cs.md)
- [`Magnetar.Protocol/Transport/AgentWireMessage.cs`](../Transport/AgentWireMessage.cs.md) — carried as the `PluginConfigSnapshot` field.
