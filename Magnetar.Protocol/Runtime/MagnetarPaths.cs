using System;
using System.Collections.Generic;
using System.IO;

namespace Magnetar.Protocol.Runtime;

public static class MagnetarPaths
{
    private const string InstallDirectoryEnvironmentVariable = "QUASAR_INSTALL_DIR";
    private const string InstallDirectoryFileEnvironmentVariable = "QUASAR_INSTALL_DIR_FILE";
    private const string DevInstallDirectoryFileName = ".quasar-install-dir";
    private static string? _cachedQuasarDirectory;

    // -------------------------------------------------------------------------
    // Root - everything lives under QUASAR_INSTALL_DIR when set. Bootstrap sets
    // it to the launcher install root for packaged installs. Direct worker
    // sessions may read an ignored .quasar-install-dir file; otherwise they use
    // the app base directory.
    // -------------------------------------------------------------------------

    public static string GetQuasarDirectory()
    {
        var cachedDirectory = _cachedQuasarDirectory;
        if (cachedDirectory is not null)
            return cachedDirectory;

        var resolvedDirectory = ResolveQuasarDirectory();
        _cachedQuasarDirectory = resolvedDirectory;
        return resolvedDirectory;
    }

    private static string ResolveQuasarDirectory()
    {
        var envOverride = Environment.GetEnvironmentVariable(InstallDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envOverride))
            return Path.GetFullPath(envOverride.Trim());

        var fileOverride = TryReadInstallDirectoryOverrideFile();
        if (fileOverride is not null)
            return fileOverride;

        return Path.GetFullPath(AppContext.BaseDirectory);
    }

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

    public static string GetQuasarBrandingDirectory() =>
        Path.Combine(GetQuasarDirectory(), "Branding");

    public static string GetQuasarDeathMessagesPath() =>
        Path.Combine(GetQuasarDirectory(), "death-messages.json");

    public static string GetQuasarWorkshopOptionsPath() =>
        Path.Combine(GetQuasarDirectory(), "steam-workshop.json");

    public static string GetQuasarGitHubUpdateCredentialsPath() =>
        Path.Combine(GetQuasarDirectory(), "github-updates.json");

    public static string GetQuasarDataProtectionKeyringDirectory() =>
        Path.Combine(GetQuasarDirectory(), "DataProtection-Keys");

    public static string GetQuasarBackupSettingsPath() =>
        Path.Combine(GetQuasarDirectory(), "backup-settings.json");

    // Folder that holds generated configuration backup ZIPs (manual + scheduled).
    public static string GetQuasarBackupsDirectory() =>
        Path.Combine(GetQuasarDirectory(), "Backups");

    // -------------------------------------------------------------------------
    // Magnetar server data  (<quasar-root>/Magnetars/<unique-name>/)
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
    // Clusters  (<quasar-root>/Clusters/<unique-name>/)
    // -------------------------------------------------------------------------

    public static string GetQuasarClustersDirectory() =>
        Path.Combine(GetQuasarDirectory(), "Clusters");

    public static string GetQuasarClusterDirectory(string uniqueName) =>
        Path.Combine(GetQuasarClustersDirectory(), SanitizePathSegment(uniqueName));

    public static string GetQuasarClusterDefinitionPath(string uniqueName) =>
        Path.Combine(GetQuasarClusterDirectory(uniqueName), "cluster.json");

    // -------------------------------------------------------------------------
    // World templates  (<quasar-root>/WorldTemplates/<id>/)
    // -------------------------------------------------------------------------

    public static string GetQuasarWorldTemplatesDirectory() =>
        Path.Combine(GetQuasarDirectory(), "WorldTemplates");

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

    public static string GetQuasarBootstrapUpdateRequestPath() =>
        Path.Combine(GetQuasarUpdatesDirectory(), "bootstrap-update-request.json");

    public static string GetQuasarWorkerRestartRequestPath() =>
        Path.Combine(GetQuasarUpdatesDirectory(), "worker-restart-request.json");

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

    // Private HOME for the managed SteamCMD process. On Linux SteamCMD resolves its Steam
    // root through ~/.steam, which on a machine with the desktop Steam client installed
    // points at the real client install and gets its library config rewritten on every
    // run. Kept persistent (not a temp dir) so SteamCMD's own update and appcache survive.
    public static string GetQuasarManagedSteamCmdHomeDirectory() =>
        Path.Combine(GetQuasarManagedRuntimeToolsDirectory(), "SteamCmdHome");

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

    private static string? TryReadInstallDirectoryOverrideFile()
    {
        foreach (var filePath in EnumerateInstallDirectoryOverrideFiles())
        {
            if (!File.Exists(filePath))
                continue;

            try
            {
                var value = File.ReadAllText(filePath).Trim();
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var baseDirectory = Path.GetDirectoryName(filePath) ?? Directory.GetCurrentDirectory();
                return Path.GetFullPath(Path.IsPathRooted(value)
                    ? value
                    : Path.Combine(baseDirectory, value));
            }
            catch
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateInstallDirectoryOverrideFiles()
    {
        var explicitFile = Environment.GetEnvironmentVariable(InstallDirectoryFileEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitFile))
        {
            var trimmed = explicitFile.Trim();
            if (Path.IsPathRooted(trimmed))
            {
                yield return trimmed;
                yield break;
            }

            foreach (var directory in EnumerateProbeDirectories())
                yield return Path.Combine(directory, trimmed);

            yield break;
        }

        foreach (var directory in EnumerateProbeDirectories())
            yield return Path.Combine(directory, DevInstallDirectoryFileName);
    }

    private static IEnumerable<string> EnumerateProbeDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var directory = new DirectoryInfo(root);
            for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
            {
                var fullPath = Path.GetFullPath(directory.FullName);
                if (seen.Add(fullPath))
                    yield return fullPath;
            }
        }
    }
}
