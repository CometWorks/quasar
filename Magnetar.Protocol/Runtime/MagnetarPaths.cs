using System;
using System.IO;

namespace Magnetar.Protocol.Runtime;

public static class MagnetarPaths
{
    // -------------------------------------------------------------------------
    // Root — everything lives under ~/.config/Quasar (Linux / macOS) or
    //        %APPDATA%\Quasar (Windows).  Override with QUASAR_DATA_DIR.
    // -------------------------------------------------------------------------

    public static string GetQuasarDirectory()
    {
        var envOverride = Environment.GetEnvironmentVariable("QUASAR_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(envOverride))
            return envOverride.Trim();

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = AppContext.BaseDirectory;

        return Path.Combine(appData, "Quasar");
    }

    // Kept for backward compatibility — resolves to the Quasar root.
    public static string GetRuntimeDirectory() => GetQuasarDirectory();

    // -------------------------------------------------------------------------
    // Bootstrap / web-service manifest
    // -------------------------------------------------------------------------

    // The manifest sits directly in the Quasar root (no WebService sub-folder).
    public static string GetWebServiceDirectory() => GetQuasarDirectory();

    public static string GetWebServiceManifestPath() =>
        Path.Combine(GetQuasarDirectory(), "service-manifest.json");

    // -------------------------------------------------------------------------
    // Quasar supervisor files
    // -------------------------------------------------------------------------

    public static string GetQuasarLogDirectory() =>
        Path.Combine(GetQuasarDirectory(), "Logs");

    public static string GetQuasarServerLogDirectory(string uniqueName) =>
        Path.Combine(GetQuasarLogDirectory(), "Magnetars", SanitizePathSegment(uniqueName));

    public static string GetQuasarSupervisorStatePath() =>
        Path.Combine(GetQuasarDirectory(), "supervisor-state.json");

    public static string GetQuasarKnownPlayersPath() =>
        Path.Combine(GetQuasarDirectory(), "known-players.json");

    public static string GetQuasarKnownPlayerSettingsPath() =>
        Path.Combine(GetQuasarDirectory(), "known-player-settings.json");

    public static string GetQuasarDiscordOptionsPath() =>
        Path.Combine(GetQuasarDirectory(), "discord.json");

    public static string GetQuasarDataHandlingConsentPath() =>
        Path.Combine(GetQuasarDirectory(), "data-handling-consent.json");

    public static string GetQuasarBrandingPath() =>
        Path.Combine(GetQuasarDirectory(), "branding.json");

    public static string GetQuasarBrandingDirectory(string webRootPath) =>
        Path.Combine(webRootPath, "branding");

    public static string GetQuasarDeathMessagesPath() =>
        Path.Combine(GetQuasarDirectory(), "death-messages.json");

    public static string GetQuasarWorkshopOptionsPath() =>
        Path.Combine(GetQuasarDirectory(), "steam-workshop.json");

    public static string GetQuasarDataProtectionKeyringDirectory() =>
        Path.Combine(GetQuasarDirectory(), "DataProtection-Keys");

    public static string GetQuasarBackupSettingsPath() =>
        Path.Combine(GetQuasarDirectory(), "backup-settings.json");

    // Folder that holds generated configuration backup ZIPs (manual + scheduled).
    public static string GetQuasarBackupsDirectory() =>
        Path.Combine(GetQuasarDirectory(), "Backups");

    // -------------------------------------------------------------------------
    // Magnetar server data  (~/.config/Quasar/Magnetars/<unique-name>/)
    // -------------------------------------------------------------------------

    /// <summary>Directory that contains one sub-folder per Magnetar server.</summary>
    public static string GetQuasarServersDirectory() =>
        Path.Combine(GetQuasarDirectory(), "Magnetars");

    public static string GetQuasarServerDirectory(string uniqueName) =>
        Path.Combine(GetQuasarServersDirectory(), SanitizePathSegment(uniqueName));

    /// <summary>
    /// Space Engineers Dedicated Server app-data for this server.
    /// Passed to the DS launcher via <c>-path</c>.
    /// </summary>
    public static string GetQuasarServerDedicatedServerAppDataDirectory(string uniqueName) =>
        Path.Combine(GetQuasarServerDirectory(uniqueName), "DedicatedServer");

    /// <summary>
    /// Magnetar app-data (profiles, sources, local config) for this server.
    /// Passed to the DS launcher via <c>-config</c>.
    /// </summary>
    public static string GetQuasarServerMagnetarAppDataDirectory(string uniqueName) =>
        Path.Combine(GetQuasarServerDirectory(uniqueName), "Magnetar");

    public static string GetQuasarServerDefinitionPath(string uniqueName) =>
        Path.Combine(GetQuasarServerDirectory(uniqueName), "server.json");

    public static string GetQuasarServerHistoryDirectory(string uniqueName) =>
        Path.Combine(GetQuasarServerDirectory(uniqueName), "History");

    public static string GetQuasarServerAnalyticsPath(string uniqueName) =>
        Path.Combine(GetQuasarServerDirectory(uniqueName), "analytics.jsonl");

    // -------------------------------------------------------------------------
    // World templates  (~/.config/Quasar/WorldTemplates/<id>/)
    // -------------------------------------------------------------------------

    public static string GetQuasarWorldTemplatesDirectory() =>
        Path.Combine(GetQuasarDirectory(), "WorldTemplates");

    public static string GetLegacyQuasarWorldProfilesDirectory() =>
        Path.Combine(GetQuasarDirectory(), "WorldProfiles");

    public static string GetQuasarWorldTemplateDirectory(string worldTemplateId) =>
        Path.Combine(GetQuasarWorldTemplatesDirectory(), SanitizePathSegment(worldTemplateId));

    public static string GetQuasarWorldTemplateDefinitionPath(string worldTemplateId) =>
        Path.Combine(GetQuasarWorldTemplateDirectory(worldTemplateId), "template.json");

    public static string GetQuasarWorldTemplateWorldDirectory(string worldTemplateId) =>
        Path.Combine(GetQuasarWorldTemplateDirectory(worldTemplateId), "World");

    public static string GetQuasarWorldTemplateHistoryDirectory(string worldTemplateId) =>
        Path.Combine(GetQuasarWorldTemplateDirectory(worldTemplateId), "History");

    // -------------------------------------------------------------------------
    // Bootstrap update / release staging
    // -------------------------------------------------------------------------

    public static string GetQuasarUpdatesDirectory() =>
        Path.Combine(GetQuasarDirectory(), "Updates");

    public static string GetQuasarStagingDirectory() =>
        Path.Combine(GetQuasarUpdatesDirectory(), "Staged");

    public static string GetQuasarActiveReleasePath() =>
        Path.Combine(GetQuasarUpdatesDirectory(), "active-release.json");

    public static string GetQuasarAppSettingsBasePath() =>
        Path.Combine(GetQuasarUpdatesDirectory(), "appsettings.base.json");

    // -------------------------------------------------------------------------
    // Managed runtime (auto-downloaded Magnetar + DS install)
    // -------------------------------------------------------------------------

    public static string GetQuasarManagedRuntimeDirectory() =>
        Path.Combine(GetQuasarDirectory(), "ManagedRuntime");

    public static string GetQuasarManagedRuntimeCacheDirectory() =>
        Path.Combine(GetQuasarManagedRuntimeDirectory(), "Cache");

    public static string GetQuasarManagedRuntimeToolsDirectory() =>
        Path.Combine(GetQuasarManagedRuntimeDirectory(), "Tools");

    public static string GetQuasarManagedWebServiceDirectory() =>
        Path.Combine(GetQuasarManagedRuntimeDirectory(), "WebService");

    public static string GetQuasarManagedWebReleaseDirectory(string version) =>
        Path.Combine(GetQuasarManagedWebServiceDirectory(), SanitizePathSegment(version));

    public static string GetQuasarManagedMagnetarInstallDirectory() =>
        Path.Combine(GetQuasarManagedRuntimeToolsDirectory(), "Magnetar");

    public static string GetQuasarManagedSteamCmdInstallDirectory() =>
        Path.Combine(GetQuasarManagedRuntimeToolsDirectory(), "SteamCMD");

    public static string GetQuasarManagedDedicatedServerInstallDirectory() =>
        Path.Combine(GetQuasarManagedRuntimeToolsDirectory(), "SpaceEngineersDedicatedServer");

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "default";

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = value.Trim();
        foreach (var invalidCharacter in invalidCharacters)
            sanitized = sanitized.Replace(invalidCharacter, '-');

        return sanitized;
    }
}
