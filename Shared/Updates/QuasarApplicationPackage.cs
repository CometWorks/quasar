using System.Text.Json;
using Magnetar.Protocol.Runtime;

namespace Quasar.Updates;

internal static class QuasarApplicationPackage
{
    internal const int UpdaterProtocol = 1;
    internal static void Validate(string directory, string version)
    {
        using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "release.json")));
        if (QuasarReleaseVersion.Normalize(metadata.RootElement.GetProperty("version").GetString()!) != version)
            throw new InvalidDataException("The package release identity does not match the requested version.");
        if (metadata.RootElement.GetProperty("updaterProtocol").GetInt32() != UpdaterProtocol)
            throw new InvalidDataException("This package needs a different updater protocol. Upgrade the updater through the platform installer first.");
        QuasarWebReleaseLayout.ValidateDirectory(directory);
        var owned = LauncherReplacement.ReadManifest(directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in owned)
            if (!File.Exists(Path.Combine(directory, name))) throw new InvalidDataException($"Incomplete application package: {name}");
        foreach (var folder in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
            if (new DirectoryInfo(folder).LinkTarget is not null) throw new InvalidDataException("Application package contains a linked directory.");
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (name is "appsettings.json" or ".verified-files.json" || name.StartsWith("Updater/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!owned.Contains(name)) throw new InvalidDataException($"The application manifest does not cover package file {name}.");
        }
    }
}
