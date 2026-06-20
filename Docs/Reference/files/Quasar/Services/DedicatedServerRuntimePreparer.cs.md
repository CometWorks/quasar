# Quasar/Services/DedicatedServerRuntimePreparer.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`DedicatedServerRuntimePreparer` transforms a `DedicatedServerDefinition` into a fully staged on-disk runtime immediately before a dedicated server process is launched. It writes the runtime DS config XML including the per-server advertised `ServerName`/`WorldName`, the Magnetar plugin sources/profile XML, the world `Sandbox_config.sbc` session settings and mod list, and the `LastSession.sbl` pointer file; deploys the bundled Quasar.Agent DLLs plus runtime-specific Harmony dependency; exposes bundled-vs-deployed agent hash comparison for manual refresh warnings; seeds the world from a template if needed; and computes the final command-line arguments string. The output is a `PreparedDedicatedServerLaunch` record.

## Structure

Namespace: `Quasar.Services`

**`DedicatedServerRuntimePreparer`** — sealed class.

| Member | Description |
|---|---|
| `PrepareAsync(DedicatedServerDefinition, dedicatedServer64Path, ct)` | Orchestrates all sub-steps; returns `PreparedDedicatedServerLaunch`. |
| `GetAgentDeploymentComparison(definition)` | Computes SHA-256 hashes for the bundled deployable `Quasar.Agent.dll` and that server's deployed Magnetar `Local/Quasar.Agent.dll`; missing deployed file is treated as drift when a bundled hash exists. |
| `PrepareRuntimeConfigAsync(...)` | Loads or creates `SpaceEngineers-Dedicated.cfg` as `XDocument`; upserts `IgnoreLastSession=false`, the game `ServerPort` and its derived `SteamPort` (`ServerPort + 1000`) / `RemoteApiPort` (`ServerPort + 2000`), IP, per-server `ServerName`/`WorldName`, all config-profile settings including block type limits, and DS-compatible password hash/salt; writes atomically. |
| `WriteLastSessionAsync(...)` | Writes `Saves/LastSession.sbl` XML pointing at the world path (absolute + relative) and uses the effective per-server world name for `GameName`. |
| `PrepareMagnetarConfigAsync(...)` | Writes `Sources/sources.xml` and `Profiles/Current.xml`; deploys the agent via `DeployQuasarAgentAsync`. |
| `BuildRemotePluginSourcesAsync(...)` | Builds `RemotePluginSourceSet` from selected plugins resolved against the catalog; refreshes the catalog once if any selection lacks a remote manifest; falls back to the default hub for unresolved plugins; always injects DotNetCompat and (on Linux) LinuxCompat core sources. |
| `PrepareWorldConfigAsync(...)` | Delegates to `WorldSandboxConfigEditor.WriteProfileAsync` to write the profile's session settings and mods authoritatively into the world's `Sandbox_config.sbc`. |
| `DeployQuasarAgentAsync(...)` | Locates `Quasar.Agent.dll` (staged `Agent/` subdir, else dev `Quasar.Agent/bin` tree up to 8 levels up); copies `Quasar.Agent.dll` + `Magnetar.Protocol.dll`; deploys the selected runtime's `0Harmony.dll`; returns the enabled local plugin file names. |
| `DeployHarmonyAsync(...)` | Copies `Agent/DotNet10/0Harmony.dll` or `Agent/NetFramework48/0Harmony.dll` to the local plugin directory as `0Harmony.dll`; Linux always uses the .NET 10 variant because only Interim is supported there. |
| `ResolveOrSeedWorldPathAsync(...)` | Uses an existing world if it has `Sandbox.sbc`; otherwise seeds from `WorldTemplateId` via `SeedWorldFromTemplateAsync`; otherwise standard validation. |
| `BuildLaunchArguments(...)` | Expands tokens, rejects `-ignorelastsession`, strips managed flags, then appends `-noconsole -daemon -path … -config … -ds64 …` plus `-consent` or `-noconsent` from Quasar's stored data-handling decision. |

**`PreparedDedicatedServerLaunch`** — sealed record: `DedicatedServerAppDataPath`, `MagnetarAppDataPath`, `DedicatedServer64Path`, `WorldPath`, `RuntimeConfigPath`, `LastSessionPath`, `Arguments`.

**`AgentDeploymentComparison`** — sealed record: bundled path/hash, deployed path/hash, `CanCompare`, and `HasMismatch`.

Private `RemotePluginSourceSet` record (`UseDefaultHub`, `Entries`). Compiled regexes strip/reject CLI flags: `-ignorelastsession`, `-console`, `-noconsole`, `-path`, `-config`, `-ds64`, `-nosplash`, `-daemon`, `-consent`, `-noconsent`, and `-withdraw-consent`.

## Structure — Magnetar config detail

`sources.xml` lists `RemoteHubSources` (default hub only when needed), `RemotePluginSources` (per-plugin manifest coordinates), `LocalPluginSources` (every dev folder from `QuasarDevFolderCatalog`), and `ModSources`. `Current.xml` lists `GitHub` plugins (manual-selection-allowed, excluding dev-folder IDs), `DevFolder` entries — **only those dev folders whose synthetic plugin id is in the profile's selected plugin set** — `Local` (the deployed agent DLLs), and an intentionally empty `Mods`.

## Dependencies

- `Quasar/Services/AtomicFileWriter.cs` — atomic XML writes
- `Quasar/Services/WebServiceOptions.cs` — `BaseUrl`, `HostId`, `DataHandlingConsentCatalog`
- `Quasar/Services/QuasarConfigProfileCatalog.cs` — profile lookup
- [`Quasar/Services/QuasarWorldTemplateCatalog.cs`](QuasarWorldTemplateCatalog.cs.md) — template lookup, world directory, seeding
- `Quasar/Services/QuasarPluginCatalogService.cs` — catalog entries, refresh, dev-folder id helpers, core-plugin constants
- `Quasar/Services/QuasarDevFolderCatalog.cs` — dev-folder selections
- `Quasar/Models/DedicatedServerDefinition.cs`, `Quasar/Models/QuasarConfigMetadata.cs`, `Quasar/Models/WorldSandboxConfigEditor.cs`
- `Magnetar.Protocol.Runtime` — `MagnetarPaths` (indirectly via catalogs)
- BCL `System.Xml.Linq`, `System.Security.Cryptography.SHA256`, `System.Reflection.AssemblyName`

## Notes

Only `SpaceEngineers-Dedicated.cfg`'s game `ServerPort` is operator-configured; `SteamPort` and `RemoteApiPort` are derived from it (`+1000` / `+2000`) whenever `ServerPort > 0` so multiple servers co-hosted on one machine never collide on the SE defaults (8766 / 8080) — a shared `SteamPort` otherwise leaves the later-starting server unreachable even though its `ServerPort` is bound. Per-server `InGameServerName` and `InGameWorldName` are written as DS root `ServerName` / `WorldName`; blank server name falls back to `DisplayName`, and blank world name falls back to `UniqueName`. Session settings, block type limits, and mods are written authoritatively into the world's `Sandbox_config.sbc`; the Magnetar profile's `<Mods>` is left empty to prevent drift. `QuasarWorldRootSettings.ServerPassword` is converted to the same PBKDF2 hash/salt shape the vanilla DS config writes (`ServerPasswordHash` / `ServerPasswordSalt`). `-ignorelastsession` is forbidden and throws if user-supplied. The `-daemon` flag detaches Magnetar from Quasar's session (Linux `setsid` / Windows `FreeConsole`) in place, so the PID and stdout/stderr pipes stay valid and managed servers survive Quasar stopping — the basis for cross-restart adoption. Launch-argument token replacement (`{uniqueName}`, `{quasarBaseUrl}`, `{hostId}`, `{worldPath}`, …) is case-insensitive. User-supplied Magnetar consent flags are stripped because Quasar owns that global decision; missing Quasar consent is treated as `-noconsent` for running instances. Missing Harmony emits a warning and disables profiler telemetry, but the agent itself can still load.
