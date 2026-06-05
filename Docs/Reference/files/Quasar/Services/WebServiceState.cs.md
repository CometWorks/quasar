# Quasar/Services/WebServiceState.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

Singleton container that groups the core runtime singletons needed throughout the Quasar web application: configuration options, the agent registry, the server catalog, and the supervisor. It also holds the mutable `CurrentManifest` that is written by `WebServiceManifestHostedService` once Kestrel is bound.

## Structure

Namespace: `Quasar.Services`

**`WebServiceState`** — sealed class, injected as singleton.

| Property | Type | Description |
|---|---|---|
| `Options` | `WebServiceOptions` | Immutable startup configuration. |
| `Registry` | `AgentRegistry` | Tracks connected Quasar.Agent WebSocket sessions. |
| `ServerCatalog` | `DedicatedServerCatalog` | Persisted catalog of all defined dedicated servers. |
| `Supervisor` | `DedicatedServerSupervisor` | Manages running server processes. |
| `CurrentManifest` | `WebServiceDiscoveryManifest` | Mutable; set by `WebServiceManifestHostedService` on startup. Defaults to `new()`. |

All properties except `CurrentManifest` are read-only (set in constructor).

## Dependencies

- [`Quasar/Services/WebServiceOptions.cs`](WebServiceOptions.cs.md)
- [`Quasar/Services/AgentRegistry.cs`](AgentRegistry.cs.md)
- [`Quasar/Services/DedicatedServerCatalog.cs`](DedicatedServerCatalog.cs.md)
- [`Quasar/Services/DedicatedServerSupervisor.cs`](DedicatedServerSupervisor.cs.md)
- `Magnetar.Protocol.Discovery` — `WebServiceDiscoveryManifest`
