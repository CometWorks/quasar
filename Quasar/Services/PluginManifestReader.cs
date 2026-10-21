using System.Xml.Linq;

namespace Quasar.Services;

/// <summary>
/// Validates a local plugin's manifest XML (the source's <c>&lt;File&gt;</c>)
/// when an admin registers a dev folder. The manifest is what Magnetar reads to
/// discover the source directories to compile and any dependencies.
/// </summary>
/// <remarks>
/// Magnetar 2.4.x reads the manifest from the <c>&lt;LocalPlugin&gt;&lt;File&gt;</c>
/// source entry and identifies the plugin by its manifest's <c>&lt;Id&gt;</c>,
/// falling back to the source folder name when no ID is provided. The generated
/// profile's <c>&lt;LocalFolderConfig&gt;&lt;Id&gt;</c> must match that identity.
/// </remarks>
public static class PluginManifestReader
{
    /// <summary>
    /// Ensures the manifest at <paramref name="manifestPath"/> exists and is
    /// well-formed XML.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown with a user-friendly message when the file is missing or cannot be
    /// parsed as XML.
    /// </exception>
    public static void ValidateManifest(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new InvalidOperationException("Manifest path is empty.");

        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"Plugin manifest not found: {manifestPath}");

        try
        {
            XDocument.Load(manifestPath);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Plugin manifest could not be parsed: {manifestPath} ({exception.Message})");
        }
    }

    public static PluginManifestMetadata ReadMetadata(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            return new PluginManifestMetadata();

        try
        {
            var root = XDocument.Load(manifestPath, LoadOptions.None).Root;
            return root is null
                ? new PluginManifestMetadata()
                : new PluginManifestMetadata(
                    GetValue(root, "FriendlyName"),
                    GetValue(root, "Author"),
                    GetValue(root, "Description"),
                    GetValue(root, "Tooltip"),
                    GetValue(root, "Runtimes"),
                    GetValue(root, "Id"));
        }
        catch
        {
            return new PluginManifestMetadata();
        }
    }

    /// <summary>
    /// The dev folder for a manifest: Magnetar compiles the manifest's
    /// <c>&lt;SourceDirectories&gt;</c> and resolves its asset paths relative to the
    /// folder, not to the manifest. A manifest in a subfolder of its repository
    /// (linux-compat's <c>ServerPlugin/ServerPlugin.xml</c>) names directories of the
    /// repository root, so the folder is the nearest one that holds all of them, and
    /// the manifest file is relative to it. Without source directories, or when no
    /// folder up to the repository root holds them, it is the manifest's own folder.
    /// </summary>
    public static (string FolderPath, string DataFile) ResolveDevFolder(string manifestPath)
    {
        var manifestFolder = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? string.Empty;
        string[] directories;
        try
        {
            directories = XDocument.Load(manifestPath).Root?.Element("SourceDirectories")?.Elements("Directory")
                .Select(directory => directory.Value.Trim()).Where(directory => directory.Length != 0).ToArray() ?? [];
        }
        catch
        {
            directories = [];
        }

        for (var folder = manifestFolder; directories.Length != 0 && !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
        {
            if (directories.All(directory => Directory.Exists(Path.Combine(folder, directory))))
                return (folder, Path.GetRelativePath(folder, Path.GetFullPath(manifestPath)));
            if (Path.Exists(Path.Combine(folder, ".git")))
                break; // never past the repository root
        }

        return (manifestFolder, Path.GetFileName(manifestPath));
    }

    private static string GetValue(XElement root, string name) =>
        root.Element(name)?.Value?.Trim() ?? string.Empty;
}

public sealed record PluginManifestMetadata(
    string FriendlyName = "",
    string Author = "",
    string Description = "",
    string Tooltip = "",
    string Runtimes = "",
    string Id = "");
