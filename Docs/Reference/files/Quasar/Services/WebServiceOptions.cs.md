# Quasar/Services/WebServiceOptions.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

Configuration record for the Quasar web service host plus the file-backed data-handling consent catalog used by managed Magnetar launches. Most `WebServiceOptions` properties are `init`-only and populated by the static factory method `Create(IConfiguration)`, which reads from the `Quasar` (or legacy `MagnetarWeb`) config section and a defined set of environment variables, with environment variables taking priority. `BackupDirectory` is mutable so the Backup page can apply a folder change to the live process after saving `Quasar:BackupDirectory`. The file also defines `DataHandlingConsentSettings` / `DataHandlingConsentCatalog`, which persist the global YES/NO consent choice in `data-handling-consent.json`.

## Structure

Namespace: `Quasar.Services`

**`WebServiceOptions`** — sealed class; most properties are `init`-only, with mutable `BackupDirectory`.

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
| `BackupDirectory` | `MagnetarPaths.GetQuasarBackupsDirectory()` | `QUASAR_BACKUP_DIR` / `Quasar:BackupDirectory` |
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
| `ResolveBackupDirectory(string?)` (static helper) | default backup dir | Returns a full backup path; relative configured values resolve under the Quasar data directory |
| `ResolveDirectoryOption(string?, string)` (static helper) | — | Shared directory resolver used by `ResolveBackupDirectory` |

**`DataHandlingConsentSettings`** — sealed DTO with nullable `ConsentGranted` (`null` = no stored decision, `true` = YES, `false` = NO), optional `DecisionDateUtc`, `Clone()`, and `Normalize(...)`.

**`DataHandlingConsentCatalog`** — sealed, `IDisposable`, JSON-backed singleton service.

| Member | Description |
|---|---|
| `Changed` | Raised after saving or external file reload. |
| `SettingsPath` | `MagnetarPaths.GetQuasarDataHandlingConsentPath()`. |
| `GetSettings()` | Returns a clone of the in-memory settings. |
| `SaveAsync(bool, CancellationToken)` | Writes the YES/NO choice and UTC decision timestamp atomically, updates the in-memory snapshot, logs, and raises `Changed`. |

Private helpers load `data-handling-consent.json`, normalize missing/invalid content to an undecided state, serialize concurrent saves with `SemaphoreSlim`, watch the file with `FileSystemWatcher`, debounce reload events by 250 ms, and compare serialized snapshots before raising `Changed`.

## Dependencies

- `Magnetar.Protocol.Runtime` — `MagnetarPaths` (default backup/log directories), `QuasarReleaseVersion` (entry assembly release identity)
- [`Quasar/Services/AtomicFileWriter.cs`](AtomicFileWriter.cs.md) — atomic writes for `data-handling-consent.json`
- `Microsoft.Extensions.Logging` — `ILogger<DataHandlingConsentCatalog>`

## Notes

`Version` is read from `AssemblyInformationalVersion` through `QuasarReleaseVersion` so prerelease labels survive packaging. `BackupDirectory` defaults to the data-directory `Backups` folder and can be moved to another disk/share; relative values are rooted under the Quasar data directory. `QuasarBackupSettingsService.SaveBackupDirectoryAsync` mutates `BackupDirectory` after patching appsettings so new stored-backup operations use the saved folder immediately. `AgentOfflineShutdownSeconds` treats zero and negative values as meaningful (agent shuts down promptly when Quasar disappears), so only unparsable/missing values fall back to 3600. `AgentProfilerMode` is normalised and used as the fallback for managed servers whose `DedicatedServerDefinition.AgentProfilerMode` is blank. Wildcard bind addresses (`0.0.0.0`, `*`, `+`) are mapped to `127.0.0.1` when constructing `BaseUrl`. `BootstrapVersion` and `LauncherToken` are populated only when the worker is launched by Quasar.Bootstrap. The legacy `MagnetarWeb` config section name is supported for backward compatibility. `DataHandlingConsentCatalog` intentionally keeps no implicit default in storage: a missing file is distinct from a stored denial, while launch code treats missing as no consent.
