using System;
using System.IO;

namespace Magnetar.Protocol.Runtime;

public static class MagnetarPaths
{
    public static string GetRuntimeDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
            appData = AppContext.BaseDirectory;

        return Path.Combine(appData, "Magnetar", "Runtime");
    }

    public static string GetWebServiceDirectory() => Path.Combine(GetRuntimeDirectory(), "WebService");

    public static string GetWebServiceManifestPath() =>
        Path.Combine(GetWebServiceDirectory(), "service-manifest.json");

    public static string GetQuasarDirectory() => Path.Combine(GetRuntimeDirectory(), "Quasar");

    public static string GetQuasarLogDirectory() => Path.Combine(GetQuasarDirectory(), "Logs");

    public static string GetQuasarInstanceLogDirectory(string instanceId) =>
        Path.Combine(GetQuasarLogDirectory(), "Instances", SanitizePathSegment(instanceId));

    public static string GetQuasarLegacyInstancesPath() =>
        Path.Combine(GetQuasarDirectory(), "instances.json");

    public static string GetQuasarInstancesDirectory() =>
        Path.Combine(GetQuasarDirectory(), "Instances");

    public static string GetQuasarInstanceDirectory(string instanceId) =>
        Path.Combine(GetQuasarInstancesDirectory(), SanitizePathSegment(instanceId));

    public static string GetQuasarInstanceDedicatedServerAppDataDirectory(string instanceId) =>
        Path.Combine(GetQuasarInstanceDirectory(instanceId), "DedicatedServer");

    public static string GetQuasarInstanceMagnetarAppDataDirectory(string instanceId) =>
        Path.Combine(GetQuasarInstanceDirectory(instanceId), "Magnetar");

    public static string GetQuasarInstanceDefinitionPath(string instanceId) =>
        Path.Combine(GetQuasarInstanceDirectory(instanceId), "instance.json");

    public static string GetQuasarInstanceHistoryDirectory(string instanceId) =>
        Path.Combine(GetQuasarInstanceDirectory(instanceId), "History");

    public static string GetQuasarUpdatesDirectory() =>
        Path.Combine(GetQuasarDirectory(), "Updates");

    public static string GetQuasarStagingDirectory() =>
        Path.Combine(GetQuasarUpdatesDirectory(), "Staged");

    public static string GetQuasarActiveReleasePath() =>
        Path.Combine(GetQuasarUpdatesDirectory(), "active-release.json");

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
