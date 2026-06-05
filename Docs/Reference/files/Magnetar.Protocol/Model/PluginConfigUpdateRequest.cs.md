# Magnetar.Protocol/Model/PluginConfigUpdateRequest.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Request DTO sent by Quasar to the agent to apply new configuration values to a specific plugin. The agent routes it to the matching `IQuasarConfigProvider` by `PluginId` and calls `ApplyConfigJson`.

## Structure
Namespace: `Magnetar.Protocol.Model`

Class `PluginConfigUpdateRequest` (concrete, no base type):

| Property | Type | Description |
|---|---|---|
| `PluginId` | `string` | Matches `IQuasarConfigProvider.PluginId` of the target plugin. |
| `ValuesJson` | `string` | Full `SaveJson` envelope or flat values object to apply. |

## Dependencies
- [`Magnetar.Protocol/Bridge/IQuasarConfigProvider.cs`](../Bridge/IQuasarConfigProvider.cs.md) — `ApplyConfigJson` is the target method.
- [`Magnetar.Protocol/Transport/AgentWireMessage.cs`](../Transport/AgentWireMessage.cs.md) — carried as the `PluginConfigUpdateRequest` field.
