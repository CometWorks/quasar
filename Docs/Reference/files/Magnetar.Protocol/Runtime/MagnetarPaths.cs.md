# Magnetar.Protocol/Runtime/MagnetarPaths.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Static helper class that centralises all filesystem path resolution for the Quasar ecosystem. The root directory defaults to `~/.config/Quasar` (Linux/macOS) or `%APPDATA%\Quasar` (Windows) and can be overridden with the `QUASAR_DATA_DIR` environment variable. Every other path in the system derives from this root.

## Structure
Namespace: `Magnetar.Protocol.Runtime`

Class `MagnetarPaths` (static):

**Root resolution**
- `GetQuasarDirectory()` — resolves root via `QUASAR_DATA_DIR` env var or `Environment.SpecialFolder.ApplicationData`\`Quasar`.
- `GetRuntimeDirectory()` — alias for `GetQuasarDirectory()` (backward compat).

**Bootstrap / discovery**
- `GetWebServiceDirectory()` — same as Quasar root.
- `GetWebServiceManifestPath()` — `<root>/service-manifest.json`.

**Supervisor-level paths** (all rooted at `GetQuasarDirectory()`)
- `GetQuasarLogDirectory()` → `Logs/`
- `GetQuasarServerLogDirectory(uniqueName)` → `Logs/Magnetars/<uniqueName>/`
- `GetQuasarSupervisorStatePath()` → `supervisor-state.json`
- `GetQuasarKnownPlayersPath()` → `known-players.json`
- `GetQuasarDiscordOptionsPath()` → `discord.json`
- `GetQuasarBrandingPath()` → `branding.json`
- `GetQuasarBrandingDirectory(webRootPath)` → `<webRoot>/branding/`
- `GetQuasarDeathMessagesPath()` → `death-messages.json`
- `GetQuasarWorkshopOptionsPath()` → `steam-workshop.json`
- `GetQuasarDataProtectionKeyringDirectory()` → `DataProtection-Keys/`

**Per-server paths** (all under `Magnetars/<uniqueName>/`)
- `GetQuasarServersDirectory()` → `Magnetars/`
- `GetQuasarServerDirectory(uniqueName)`
- `GetQuasarServerDedicatedServerAppDataDirectory(uniqueName)` → `.../DedicatedServer/`
- `GetQuasarServerMagnetarAppDataDirectory(uniqueName)` → `.../Magnetar/`
- `GetQuasarServerDefinitionPath(uniqueName)` → `.../server.json`
- `GetQuasarServerHistoryDirectory(uniqueName)` → `.../History/`
- `GetQuasarServerAnalyticsPath(uniqueName)` → `.../analytics.jsonl`

**World templates** (under `WorldTemplates/<id>/`)
- `GetQuasarWorldTemplatesDirectory()`
- `GetLegacyQuasarWorldProfilesDirectory()` — legacy `WorldProfiles/`
- `GetQuasarWorldTemplateDirectory(id)`, `GetQuasarWorldTemplateDefinitionPath(id)`, `GetQuasarWorldTemplateWorldDirectory(id)`, `GetQuasarWorldTemplateHistoryDirectory(id)`

**Bootstrap updates** (under `Updates/`)
- `GetQuasarUpdatesDirectory()`, `GetQuasarStagingDirectory()` → `Updates/Staged/`, `GetQuasarActiveReleasePath()` → `Updates/active-release.json`

**Managed runtime** (under `ManagedRuntime/`)
- `GetQuasarManagedRuntimeDirectory()`, `..CacheDirectory()`, `..ToolsDirectory()`
- `GetQuasarManagedMagnetarInstallDirectory()` → `Tools/Magnetar/`
- `GetQuasarManagedSteamCmdInstallDirectory()` → `Tools/SteamCMD/`
- `GetQuasarManagedDedicatedServerInstallDirectory()` → `Tools/SpaceEngineersDedicatedServer/`

Private `SanitizePathSegment(value)` — replaces `Path.GetInvalidFileNameChars()` with `-`.

## Dependencies
- [`Magnetar.Protocol/Discovery/WebServiceDiscoveryManifest.cs`](../Discovery/WebServiceDiscoveryManifest.cs.md) — manifest path consumed here.
- [`Magnetar.Protocol/Runtime/QuasarActiveReleasePointer.cs`](QuasarActiveReleasePointer.cs.md) — file pointed to by `GetQuasarActiveReleasePath()`.

## Notes
- `QUASAR_DATA_DIR` override enables containerised / multi-tenant deployments on Linux.
- `SanitizePathSegment` is `private` — unique names must be sanitized before being embedded in any path.
- `GetLegacyQuasarWorldProfilesDirectory()` is retained for migration support only.
