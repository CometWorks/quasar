# Magnetar.Protocol/Discovery/WebServiceDiscoveryManifest.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Plain DTO written by the Quasar supervisor to `service-manifest.json` (path resolved by `MagnetarPaths.GetWebServiceManifestPath()`) so that Quasar.Bootstrap and other local processes can discover the running Quasar web service without a pre-configured port.

## Structure
Namespace: `Magnetar.Protocol.Discovery`

Class `WebServiceDiscoveryManifest` (concrete, no base type):

| Property | Type | Description |
|---|---|---|
| `WorkerId` | `string` | Unique ID for this Quasar supervisor worker instance. |
| `HostId` | `string` | Identifier of the hosting machine within a multi-host setup. |
| `MachineName` | `string` | `Environment.MachineName` of the host. |
| `ProcessId` | `int` | PID of the Quasar supervisor process. |
| `BaseUrl` | `string` | HTTP(S) base URL the Quasar web server is listening on (e.g. `http://localhost:5000`). |
| `StartedAtUtc` | `DateTimeOffset` | UTC time when the supervisor started. |

## Dependencies
- [`Magnetar.Protocol/Runtime/MagnetarPaths.cs`](../Runtime/MagnetarPaths.cs.md) — `GetWebServiceManifestPath()` resolves the path where this manifest is read/written.

## Notes
The manifest file is written to the Quasar root directory (not a sub-folder) for easy discovery by Bootstrap without any shared configuration.
