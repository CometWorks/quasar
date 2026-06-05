# Magnetar.Protocol/Model/PluginRuntimeInfo.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Lightweight DTO describing a single loaded plugin reported in `AgentSnapshot.Plugins`. Used by the Quasar UI to display the plugin roster and its load state.

## Structure
Namespace: `Magnetar.Protocol.Model`

Class `PluginRuntimeInfo` (concrete, no base type):

| Property | Type | Description |
|---|---|---|
| `PluginId` | `string` | Unique plugin identifier. |
| `DisplayName` | `string` | Human-readable plugin name. |
| `Version` | `string` | Plugin version string. |
| `IsLoaded` | `bool` | Whether the plugin is currently active/loaded. |

## Dependencies
- [`Magnetar.Protocol/Model/AgentSnapshot.cs`](AgentSnapshot.cs.md) — listed in `Plugins`.
