# Quasar/Services/PluginSdk/PluginConfigService.cs

**Module:** Quasar.Services.PluginSdk  **Kind:** class  **Tier:** 2

## Summary

`IHostedService` that caches the plugin configuration snapshots reported by connected agents and routes config-update commands back to them over WebSocket. Subscribes to `AgentRegistry.Changed` to evict stale cache entries when agents disconnect, and raises `Changed` for Blazor reactivity. Follows the same catalog/service pattern as other Quasar runtime services.

## Structure

Namespace: `Quasar.Services.PluginSdk`

**`PluginConfigService`** (sealed class) — implements `IHostedService`

Event:
- `Changed : Action?` — fired when cached data changes (snapshot ingested or stale agent removed)

Lifecycle:
- `StartAsync` — subscribes `HandleRegistryChanged` to `AgentRegistry.Changed`
- `StopAsync` — unsubscribes

Public API:
- `IngestSnapshot(PluginConfigSnapshot)` — stores the plugin list from an agent's snapshot message; calls `NotifyChanged`
- `GetConfigsForAgent(string agentId) : IReadOnlyList<PluginConfigData>` — returns a shallow copy of cached configs for one agent
- `HasConfigs(string agentId) : bool` — true when the agent has at least one configurable plugin cached
- `UpdatePluginConfigAsync(string agentId, string pluginId, string valuesJson, CancellationToken) : Task` — sends a `PluginConfigUpdate` wire message to the agent via `AgentRegistry.SendToAgentAsync`

Private:
- `HandleRegistryChanged()` — removes entries for agents no longer reported as connected; fires `NotifyChanged` if any removed
- `Clone(PluginConfigData) : PluginConfigData` — shallow copy to avoid callers mutating cached state

## Dependencies

- [`Quasar/Services/AgentRegistry.cs`](../AgentRegistry.cs.md)
- `Magnetar.Protocol.Model.PluginConfigSnapshot`, `PluginConfigData`, `PluginConfigUpdateRequest` (external package `Magnetar.Protocol`)
- `Magnetar.Protocol.Transport.AgentWireMessage`, `WireMessageKind` (external package `Magnetar.Protocol`)

## Notes

- All cache reads and writes are guarded by `_sync`; `NotifyChanged` is called outside the lock to avoid holding the lock during event dispatch.
- `GetConfigsForAgent` returns cloned `PluginConfigData` objects but only copies the three known fields (`PluginId`, `DisplayName`, `ConfigJson`), so any other fields on the protocol type are silently dropped.
