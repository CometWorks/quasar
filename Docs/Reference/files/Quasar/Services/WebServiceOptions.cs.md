# Quasar/Services/WebServiceOptions.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

Immutable configuration record for the Quasar web service host. All properties are `init`-only and populated by the static factory method `Create(IConfiguration)`, which reads from the `Quasar` (or legacy `MagnetarWeb`) config section and a defined set of environment variables, with environment variables taking priority. It also carries agent launch settings that the supervisor forwards to each managed dedicated server process.

## Structure

Namespace: `Quasar.Services`

**`WebServiceOptions`** — sealed class, all properties `init`-only.

| Property | Default | Env var / config key |
|---|---|---|
| `Host` | `"0.0.0.0"` | `QUASAR_WEB_HOST` |
| `Port` | `8080` | `QUASAR_WEB_PORT` |
| `WorkerId` | new GUID | — (per-process) |
| `HostId` | machine name (lower) | `QUASAR_HOST_ID` / `MAGNETAR_HOST_ID` |
| `HostName` | machine name | — |
| `BaseUrl` | derived from Host/Port | `QUASAR_PUBLIC_BASE_URL` |
| `ListenUrl` | derived | — |
| `Version` | entry assembly release identity | — |
| `BootstrapVersion` | empty | `QUASAR_BOOTSTRAP_VERSION` |
| `Mode` | `"Console"` | `QUASAR_MODE` |
| `OpenBrowserOnStart` | `true` | `QUASAR_OPEN_BROWSER_ON_START` |
| `LoggingDirectory` | default log dir | `QUASAR_LOG_DIR` |
| `LoggingFormat` | `"text"` | `QUASAR_LOG_FORMAT` |
| `LoggingMinimumLevel` | `"Info"` | `QUASAR_LOG_MIN_LEVEL` |
| `IsDevelopment` | false | `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` |
| `DisableServerHealthMonitoring` | false (true in dev) | `QUASAR_DISABLE_SERVER_HEALTH_MONITORING` |
| `OwnManifest` | `true` | `QUASAR_OWN_MANIFEST` |
| `PreserveManagedServersOnShutdown` | `true` | `QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN` |
| `AgentOfflineShutdownSeconds` | `3600` | `QUASAR_AGENT_OFFLINE_SHUTDOWN_SECONDS` |
| `AgentReconnectIntervalSeconds` | `10` | `QUASAR_AGENT_RECONNECT_INTERVAL_SECONDS` |
| `AgentReconnectJitterSeconds` | `3` | `QUASAR_AGENT_RECONNECT_JITTER_SECONDS` |
| `AgentProfilerMode` | `"SafeContinuous"` | `QUASAR_AGENT_PROFILER_MODE` / `AgentProfilerMode` |
| `AvoidSimultaneousScheduledRestarts` | `true` | `QUASAR_AVOID_SIMULTANEOUS_SCHEDULED_RESTARTS` |
| `LauncherToken` | empty | `QUASAR_LAUNCHER_TOKEN` |
| `IsServiceMode` (computed) | — | `true` when `Mode == "service"` |
| `SupervisorName` (const) | `"Quasar"` | Written to stdout on startup |
| `Create(IConfiguration)` (static) | — | Factory method |

## Dependencies

- `Magnetar.Protocol.Runtime` — `MagnetarPaths` (default log directory), `QuasarReleaseVersion` (entry assembly release identity)

## Notes

`Version` is read from `AssemblyInformationalVersion` through `QuasarReleaseVersion` so prerelease labels survive packaging. `AgentOfflineShutdownSeconds` treats zero and negative values as meaningful (agent shuts down promptly when Quasar disappears), so only unparsable/missing values fall back to 3600. `AgentProfilerMode` is normalised and used as the fallback for managed servers whose `DedicatedServerDefinition.AgentProfilerMode` is blank. Wildcard bind addresses (`0.0.0.0`, `*`, `+`) are mapped to `127.0.0.1` when constructing `BaseUrl`. `BootstrapVersion` is populated only when the worker is launched by Quasar.Bootstrap. The legacy `MagnetarWeb` config section name is supported for backward compatibility.
