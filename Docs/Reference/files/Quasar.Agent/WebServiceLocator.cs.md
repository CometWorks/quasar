# Quasar.Agent/WebServiceLocator.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
`WebServiceLocator` resolves the base URI of the running Quasar web service. It reads the `WebServiceDiscoveryManifest` written by the supervisor, health-checks the `/api/health` endpoint, and if no healthy instance is found, attempts to launch `Quasar.Bootstrap` to start one — using a named mutex (`Quasar.Bootstrap`) to avoid concurrent spawn races. It then polls for up to 30 s for the service to become healthy.

## Structure
**Namespace:** `Quasar.Agent`  
**Modifiers:** public, concrete

| Member | Description |
|---|---|
| `EnsureWebServiceAsync(CancellationToken)` | Top-level entry: checks health, acquires mutex, optionally spawns bootstrap, polls until healthy; returns `Uri` or `null` |
| `TryRunBootstrapAsync` (private) | Builds launch spec, starts the bootstrap process, polls for exit (up to 45 s), returns `true` on exit code 0 |
| `TryGetHealthyServiceUriAsync` (private) | Reads manifest, validates URL, HTTP-GETs `/api/health` with 2 s timeout, returns base `Uri` on HTTP 200 |
| `ReadManifest` (private, static) | Reads `MagnetarPaths.GetWebServiceManifestPath()`, deserializes to `WebServiceDiscoveryManifest` |
| `TryBuildBootstrapLaunchSpec` (private, static) | Resolves bootstrap binary in priority order: `QUASAR_BOOTSTRAP_EXE` env → `QUASAR_BOOTSTRAP_DLL` env → `Quasar.Bootstrap.dll` sibling search → `Quasar.Bootstrap.exe` → `Quasar.exe`/`Quasar` |
| `FindCandidate` (private, static) | Walks up directory tree (max 8 levels) looking for the named file directly or under a `Quasar.Bootstrap/bin` subtree |

**Bootstrap arguments used:** `ensure-running --quiet`

## Dependencies
- `Magnetar.Protocol.Discovery` — `WebServiceDiscoveryManifest`
- `Magnetar.Protocol.Runtime` — `MagnetarPaths`
- `Newtonsoft.Json` — manifest deserialization

## Notes
The named mutex `Quasar.Bootstrap` prevents multiple agents on the same machine from racing to spawn the bootstrap when Quasar is not yet running. After acquiring the mutex, the locator re-checks health before spawning, so the first agent to get the mutex will start bootstrap while others will see the healthy service once they re-check. The HTTP health check uses the legacy `WebRequest` API (netstandard2.0 constraint).
