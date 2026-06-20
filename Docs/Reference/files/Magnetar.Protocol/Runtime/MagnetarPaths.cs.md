# Magnetar.Protocol/Runtime/MagnetarPaths.cs

**Module:** Magnetar.Protocol  **Kind:** class (static)  **Tier:** 1

## Summary
Central, shared resolver for every on-disk path Quasar and its helpers use. All locations hang off a single Quasar data root (`~/.config/Quasar` on Linux/macOS, `%APPDATA%\Quasar` on Windows), overridable via the `QUASAR_DATA_DIR` environment variable. Used by the supervisor, bootstrap launcher, and other components so they agree on file layout.

## Structure
Namespace `Magnetar.Protocol.Runtime`; `public static class MagnetarPaths`. Pure path-composition helpers (no I/O); user-supplied name segments are run through `SanitizePathSegment`.

- Root: `GetQuasarDirectory()` (env override → `ApplicationData` → `AppContext.BaseDirectory`), `GetRuntimeDirectory()` (back-compat alias of the root).
- Web-service manifest: `GetWebServiceDirectory()` (the root itself), `GetWebServiceManifestPath()` → `service-manifest.json`.
- Supervisor files: `GetQuasarLogDirectory()`, `GetQuasarServerLogDirectory(uniqueName)` → `Logs/Magnetars/<uniqueName>/`, `GetQuasarSupervisorStatePath()`, `GetQuasarKnownPlayersPath()` → `known-players.json`, `GetQuasarKnownPlayerSettingsPath()` → `known-player-settings.json`, `GetQuasarDiscordOptionsPath()`, `GetQuasarDataHandlingConsentPath()` → `data-handling-consent.json`, `GetQuasarBrandingPath()`, `GetQuasarBrandingDirectory(webRootPath)`, `GetQuasarDeathMessagesPath()`, `GetQuasarWorkshopOptionsPath()`, `GetQuasarDataProtectionKeyringDirectory()`, `GetQuasarBackupSettingsPath()` → `backup-settings.json`, `GetQuasarBackupsDirectory()` → default `Backups/` storage folder used when `Quasar:BackupDirectory` is empty.
- Per-Magnetar server data (`Magnetars/<unique-name>/`): `GetQuasarServersDirectory()`, `GetQuasarServerDirectory()`, `GetQuasarServerDedicatedServerAppDataDirectory()` (DS `-path`), `GetQuasarServerMagnetarAppDataDirectory()` (DS `-config`), `GetQuasarServerDefinitionPath()` → `server.json`, `GetQuasarServerHistoryDirectory()`, `GetQuasarServerAnalyticsPath()` → `analytics.jsonl`.
- World templates (`WorldTemplates/<id>/`): `GetQuasarWorldTemplatesDirectory()`, `GetLegacyQuasarWorldProfilesDirectory()` (legacy `WorldProfiles/`), `GetQuasarWorldTemplateDirectory()`, `GetQuasarWorldTemplateDefinitionPath()` → `template.json`, `GetQuasarWorldTemplateWorldDirectory()`, `GetQuasarWorldTemplateHistoryDirectory()`.
- Bootstrap update/release staging (`Updates/`): `GetQuasarUpdatesDirectory()`, `GetQuasarStagingDirectory()` → `Updates/Staged/`, `GetQuasarActiveReleasePath()` → `Updates/active-release.json`, `GetQuasarAppSettingsBasePath()` → `Updates/appsettings.base.json` (release-base ancestor for appsettings rollover), `GetQuasarBootstrapUpdateRequestPath()` → `Updates/bootstrap-update-request.json` (worker request consumed by Bootstrap to run launcher self-update immediately).
- Managed runtime (`ManagedRuntime/`): `GetQuasarManagedRuntimeDirectory()`, `GetQuasarManagedRuntimeCacheDirectory()`, `GetQuasarManagedRuntimeToolsDirectory()`, `GetQuasarManagedWebServiceDirectory()` → `WebService/`, `GetQuasarManagedWebReleaseDirectory(version)` → `WebService/<version>/`, `GetQuasarManagedMagnetarInstallDirectory()` → `Tools/Magnetar/`, `GetQuasarManagedSteamCmdInstallDirectory()` → `Tools/SteamCMD/`, `GetQuasarManagedDedicatedServerInstallDirectory()` → `Tools/SpaceEngineersDedicatedServer/`.
- Private `SanitizePathSegment(value)`: trims, replaces invalid filename chars with `-`, returns `default` for empty input.

## Dependencies
- [`Magnetar.Protocol/Discovery/WebServiceDiscoveryManifest.cs`](../Discovery/WebServiceDiscoveryManifest.cs.md) — file resolved by `GetWebServiceManifestPath()`.
- [`Magnetar.Protocol/Runtime/QuasarActiveReleasePointer.cs`](QuasarActiveReleasePointer.cs.md) — file resolved by `GetQuasarActiveReleasePath()`.
- `Quasar/Services/Updates/QuasarUpdateService.cs` — uses `GetQuasarAppSettingsBasePath()` as the stored merge base for staged `appsettings.json` rollover and writes `GetQuasarBootstrapUpdateRequestPath()` for forced launcher activation.
- `System`, `System.IO` (BCL only).

## Notes
Cross-platform by design; the `QUASAR_DATA_DIR` override is the single switch to relocate all state (e.g. containerised/multi-tenant deployments). Name segments (server unique names, world template ids, managed web release versions) must be sanitized before becoming directory names — `SanitizePathSegment` is private, so callers go through the typed helpers. `GetLegacyQuasarWorldProfilesDirectory()` exists only for migration.
