using Quasar.Models;

namespace Quasar.Services;

internal static class ConfigCatalogRules
{
    public const string DotNetCompatPluginId = "dotnet-compat";
    public const string LinuxCompatPluginId = "linux-compat";
    public const string ErrorReportingPluginId = "error-reporting";
    public static readonly string[] CorePluginIds = [DotNetCompatPluginId, LinuxCompatPluginId, ErrorReportingPluginId];
    // LEGACY-MAGNETAR-COMPAT: Magnetar before 2.3.3.0 force-loads the se- prefixed ids,
    // which the hub keeps in the *LegacyId.xml manifests. Remove with the Legacy style in
    // the first 2027 Quasar release.
    public const string LegacyDotNetCompatPluginId = "se-dotnet-compat";
    public const string LegacyLinuxCompatPluginId = "se-linux-compat";
    public const string DefaultHubName = "MagnetarHub";
    public const string DefaultHubRepo = "CometWorks/magnetar-hub";
    public const string DefaultHubBranch = "main";

    public static bool IsManualSelectionAllowed(string pluginId)
    {
        var id = pluginId?.Trim();
        return !string.Equals(id, DotNetCompatPluginId, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(id, ErrorReportingPluginId, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(id, LegacyDotNetCompatPluginId, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetRepositoryUrl(string sourceRepo)
    {
        var repo = sourceRepo?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(repo))
            return string.Empty;

        if (Uri.TryCreate(repo, UriKind.Absolute, out var uri) &&
            (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return uri.ToString();
        }

        return repo.Contains('/', StringComparison.Ordinal)
            ? $"https://github.com/{repo.Trim('/')}"
            : string.Empty;
    }

    public static bool IsDefaultHubEntry(QuasarPluginCatalogEntry entry) =>
        !entry.IsLocalDevFolder &&
        (string.IsNullOrEmpty(entry.HubName) || string.Equals(entry.HubName, DefaultHubName, StringComparison.Ordinal));

    public static string GetDevFolderPluginId(QuasarDevFolderSelection devFolder)
    {
        var pluginId = devFolder.PluginId?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(pluginId)
            ? devFolder.SourceFolderName
            : pluginId;
    }
}
