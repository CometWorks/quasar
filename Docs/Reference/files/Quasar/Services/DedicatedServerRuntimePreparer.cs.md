# Quasar/Services/DedicatedServerRuntimePreparer.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`DedicatedServerRuntimePreparer` transforms a `DedicatedServerDefinition` into a fully staged on-disk runtime immediately before a dedicated server process is launched. It writes the runtime DS config XML, the Magnetar plugin sources/profile XML, the world mod list, and the `LastSession.sbl` pointer file; deploys the Quasar.Agent DLL; and computes the final command-line arguments string. The output is a `PreparedDedicatedServerLaunch` record.

## Structure

Namespace: `Quasar.Services`

**`DedicatedServerRuntimePreparer`** — sealed class.

| Member | Description |
|---|---|
| `PrepareAsync(DedicatedServerDefinition, dedicatedServer64Path, ct)` | Orchestrates all sub-steps; returns `PreparedDedicatedServerLaunch`. |
| `PrepareRuntimeConfigAsync(...)` | Loads or creates `SpaceEngineers-Dedicated.cfg` as `XDocument`; upserts `IgnoreLastSession`, port, IP, and all config-profile settings; writes atomically. |
| `WriteLastSessionAsync(...)` | Writes `LastSession.sbl` XML pointing to the world path. |
| `PrepareMagnetarConfigAsync(...)` | Writes `sources.xml` (remote hub/plugin/dev-folder/mod sources) and `Current.xml` profile; deploys the agent via `DeployQuasarAgentAsync`. |
| `PrepareWorldModListAsync(...)` | Delegates to `WorldSandboxConfigEditor.WriteModsAsync` to update `Sandbox_config.sbc`. |
| `BuildLaunchArguments(...)` | Strips managed args (`-path`, `-config`, `-ds64`, `-console`, `-nosplash`), expands `{uniqueName}`/`{configPath}`/`{quasarBaseUrl}`/`{hostId}` etc. tokens, then appends `-noconsole -path … -config … -ds64 …`. |
| `DeployQuasarAgentAsync(...)` | Locates `Quasar.Agent.dll` (staged `Agent/` subdir or dev build tree up to 8 levels); copies changed files using SHA-256 comparison. |
| `BuildRemotePluginSourcesAsync(...)` | Refreshes plugin catalog if needed; constructs `RemotePluginSourceSet` with per-plugin manifest coordinates; always injects DotNetCompat and (on Linux) LinuxCompat core plugins. |
| `SeedWorldFromTemplateAsync(...)` | Copies world template files into the server world directory (no-overwrite). |

**`PreparedDedicatedServerLaunch`** — sealed record with paths: `DedicatedServerAppDataPath`, `MagnetarAppDataPath`, `DedicatedServer64Path`, `WorldPath`, `RuntimeConfigPath`, `LastSessionPath`, `Arguments`.

Key compiled regexes strip or reject specific CLI flags (`-ignorelastsession`, `-console`, `-noconsole`, `-path`, `-config`, `-ds64`, `-nosplash`).

## Dependencies

- `Quasar/Services/AtomicFileWriter.cs` — all atomic file writes
- [`Quasar/Services/WebServiceOptions.cs`](WebServiceOptions.cs.md) — `BaseUrl`, `HostId`
- `Quasar/Services/QuasarConfigProfileCatalog.cs` — profile lookup
- `Quasar/Services/QuasarWorldTemplateCatalog.cs` — template lookup and world directory
- `Quasar/Services/QuasarPluginCatalogService.cs` — plugin catalog and dev folder IDs
- `Quasar/Services/QuasarDevFolderCatalog.cs` — dev folder selections
- [`Quasar/Models/DedicatedServerDefinition.cs`](../Models/DedicatedServerDefinition.cs.md) — input definition
- `Quasar/Models/QuasarConfigMetadata.cs` — config option enumeration and formatting
- `Quasar/Models/WorldSandboxConfigEditor.cs` — `WriteModsAsync`
- `Magnetar.Protocol.Runtime` — `MagnetarPaths`
- BCL `System.Xml.Linq`, `System.Security.Cryptography.SHA256`

## Notes

Mods are written authoritatively into the world's `Sandbox_config.sbc` by `PrepareWorldModListAsync`; the Magnetar profile's `<Mods>` element is intentionally left empty to prevent drift. The agent DLL copy uses SHA-256 comparison to avoid unnecessary writes. The `-ignorelastsession` flag is explicitly forbidden and throws if present in user-supplied launch arguments. Launch argument tokens (`{quasarBaseUrl}`, `{hostId}`, etc.) use case-insensitive replacement.
