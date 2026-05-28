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

    public static string GetQuasarInstanceLogDirectory(string instanceId) =>
        Path.Combine(GetQuasarLogDirectory(), "Instances", SanitizePathSegment(instanceId));

    public static string GetQuasarSupervisorStatePath() =>
        Path.Combine(GetQuasarDirectory(), "supervisor-state.json");

    public static string GetQuasarKnownPlayersPath() =>
        Path.Combine(GetQuasarDirectory(), "known-players.json");

    public static string GetQuasarDiscordOptionsPath() =>
        Path.Combine(GetQuasarDirectory(), "discord.json");

    public static string GetQuasarDeathMessagesPath() =>
        Path.Combine(GetQuasarDirectory(), "death-messages.json");

    // -------------------------------------------------------------------------
    // Magnetar instance data  (~/.config/Quasar/Magnetars/<id>/)
    // -------------------------------------------------------------------------

    /// <summary>Legacy flat-file path — only used as a fallback migration read.</summary>
    public static string GetQuasarLegacyInstancesPath() =>
        Path.Combine(GetQuasarDirectory(), "instances.json");

    /// <summary>Directory that contains one sub-folder per Magnetar instance.</summary>
    public static string GetQuasarInstancesDirectory() =>
        Path.Combine(GetQuasarDirectory(), "Magnetars");

    public static string GetQuasarInstanceDirectory(string instanceId) =>
        Path.Combine(GetQuasarInstancesDirectory(), SanitizePathSegment(instanceId));

    /// <summary>
    /// Space Engineers Dedicated Server app-data for this instance.
    /// Passed to the DS launcher via <c>-path</c>.
    /// </summary>
    public static string GetQuasarInstanceDedicatedServerAppDataDirectory(string instanceId) =>
        Path.Combine(GetQuasarInstanceDirectory(instanceId), "DedicatedServer");

    /// <summary>
    /// Magnetar app-data (profiles, sources, local config) for this instance.
    /// Passed to the DS launcher via <c>-config</c>.
    /// </summary>
    public static string GetQuasarInstanceMagnetarAppDataDirectory(string instanceId) =>
        Path.Combine(GetQuasarInstanceDirectory(instanceId), "Magnetar");

    public static string GetQuasarInstanceDefinitionPath(string instanceId) =>
        Path.Combine(GetQuasarInstanceDirectory(instanceId), "instance.json");

    public static string GetQuasarInstanceHistoryDirectory(string instanceId) =>
        Path.Combine(GetQuasarInstanceDirectory(instanceId), "History");

    public static string GetQuasarInstanceAnalyticsPath(string instanceId) =>
        Path.Combine(GetQuasarInstanceDirectory(instanceId), "analytics.json");

    // -------------------------------------------------------------------------
    // World profiles  (~/.config/Quasar/WorldProfiles/<id>/)
    // -------------------------------------------------------------------------

    public static string GetQuasarWorldProfilesDirectory() =>
        Path.Combine(GetQuasarDirectory(), "WorldProfiles");

    public static string GetQuasarWorldProfileDirectory(string worldProfileId) =>
        Path.Combine(GetQuasarWorldProfilesDirectory(), SanitizePathSegment(worldProfileId));

    public static string GetQuasarWorldProfileDefinitionPath(string worldProfileId) =>
        Path.Combine(GetQuasarWorldProfileDirectory(worldProfileId), "profile.json");

    public static string GetQuasarWorldProfileWorldDirectory(string worldProfileId) =>
        Path.Combine(GetQuasarWorldProfileDirectory(worldProfileId), "World");

    public static string GetQuasarWorldProfileHistoryDirectory(string worldProfileId) =>
        Path.Combine(GetQuasarWorldProfileDirectory(worldProfileId), "History");

    // -------------------------------------------------------------------------
    // Bootstrap update / release staging
    // -------------------------------------------------------------------------

    public static string GetQuasarUpdatesDirectory() =>
        Path.Combine(GetQuasarDirectory(), "Updates");

    public static string GetQuasarStagingDirectory() =>
        Path.Combine(GetQuasarUpdatesDirectory(), "Staged");

    public static string GetQuasarActiveReleasePath() =>
        Path.Combine(GetQuasarUpdatesDirectory(), "active-release.json");

    // -------------------------------------------------------------------------
    // Managed runtime (auto-downloaded Magnetar + DS install)
    // -------------------------------------------------------------------------

    public static string GetQuasarManagedRuntimeDirectory() =>
        Path.Combine(GetQuasarDirectory(), "ManagedRuntime");

    public static string GetQuasarManagedRuntimeCacheDirectory() =>
        Path.Combine(GetQuasarManagedRuntimeDirectory(), "Cache");

    public static string GetQuasarManagedRuntimeToolsDirectory() =>
        Path.Combine(GetQuasarManagedRuntimeDirectory(), "Tools");

    public static string GetQuasarManagedMagnetarInstallDirectory() =>
        Path.Combine(GetQuasarManagedRuntimeToolsDirectory(), "Magnetar");

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
