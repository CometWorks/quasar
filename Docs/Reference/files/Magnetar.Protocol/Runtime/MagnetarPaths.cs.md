# Magnetar.Protocol/Runtime/MagnetarPaths.cs

**Module:** Magnetar.Protocol  **Kind:** class (static)  **Tier:** 1

## Summary
Central, shared resolver for every on-disk path Quasar and its helpers use. All locations hang off a single Quasar data root (`~/.config/Quasar` on Linux/macOS, `%APPDATA%\Quasar` on Windows), overridable via the `QUASAR_DATA_DIR` environment variable. Used by the supervisor, bootstrap launcher, and other components so they agree on file layout.

## Structure
Namespace `Magnetar.Protocol.Runtime`; `public static class MagnetarPaths`. Pure path-composition helpers (no I/O); user-supplied name segments are run through `SanitizePathSegment`.

- Root: `GetQuasarDirectory()` (env override → `ApplicationData` → `AppContext.BaseDirectory`), `GetRuntimeDirectory()` (back-compat alias of the root).
- Web-service manifest: `GetWebServiceDirectory()` (the root itself), `GetWebServiceManifestPath()` → `service-manifest.json`.
- Supervisor files: `GetQuasarLogDirectory()`, `GetQuasarServerLogDirectory(uniqueName)` → `Logs/Magnetars/<uniqueName>/`, `GetQuasarSupervisorStatePath()`, `GetQuasarKnownPlayersPath()`, `GetQuasarDiscordOptionsPath()`, `GetQuasarBrandingPath()`, `GetQuasarBrandingDirectory(webRootPath)`, `GetQuasarDeathMessagesPath()`, `GetQuasarWorkshopOptionsPath()`, `GetQuasarDataProtectionKeyringDirectory()`, `GetQuasarBackupSettingsPath()` → `backup-settings.json`, `GetQuasarBackupsDirectory()` → `Backups/` (generated configuration backup ZIPs, manual + scheduled).
- Per-Magnetar server data (`Magnetars/<unique-name>/`): `GetQuasarServersDirectory()`, `GetQuasarServerDirectory()`, `GetQuasarServerDedicatedServerAppDataDirectory()` (DS `-path`), `GetQuasarServerMagnetarAppDataDirectory()` (DS `-config`), `GetQuasarServerDefinitionPath()` → `server.json`, `GetQuasarServerHistoryDirectory()`, `GetQuasarServerAnalyticsPath()` → `analytics.jsonl`.
- World templates (`WorldTemplates/<id>/`): `GetQuasarWorldTemplatesDirectory()`, `GetLegacyQuasarWorldProfilesDirectory()` (legacy `WorldProfiles/`), `GetQuasarWorldTemplateDirectory()`, `GetQuasarWorldTemplateDefinitionPath()` → `template.json`, `GetQuasarWorldTemplateWorldDirectory()`, `GetQuasarWorldTemplateHistoryDirectory()`.
- Bootstrap update/release staging (`Updates/`): `GetQuasarUpdatesDirectory()`, `GetQuasarStagingDirectory()` → `Updates/Staged/`, `GetQuasarActiveReleasePath()` → `Updates/active-release.json`.
- Managed runtime (`ManagedRuntime/`): `GetQuasarManagedRuntimeDirectory()`, `GetQuasarManagedRuntimeCacheDirectory()`, `GetQuasarManagedRuntimeToolsDirectory()`, `GetQuasarManagedMagnetarInstallDirectory()` → `Tools/Magnetar/`, `GetQuasarManagedSteamCmdInstallDirectory()` → `Tools/SteamCMD/`, `GetQuasarManagedDedicatedServerInstallDirectory()` → `Tools/SpaceEngineersDedicatedServer/`.
- Private `SanitizePathSegment(value)`: trims, replaces invalid filename chars with `-`, returns `default` for empty input.

## Dependencies
- `Magnetar.Protocol/Discovery/WebServiceDiscoveryManifest.cs` — file resolved by `GetWebServiceManifestPath()`.
- `Magnetar.Protocol/Runtime/QuasarActiveReleasePointer.cs` — file resolved by `GetQuasarActiveReleasePath()`.
- `System`, `System.IO` (BCL only).

## Notes
Cross-platform by design; the `QUASAR_DATA_DIR` override is the single switch to relocate all state (e.g. containerised/multi-tenant deployments). Name segments (server unique names, world template ids) must be sanitized before becoming directory names — `SanitizePathSegment` is private, so callers go through the typed helpers. `GetLegacyQuasarWorldProfilesDirectory()` exists only for migration.
