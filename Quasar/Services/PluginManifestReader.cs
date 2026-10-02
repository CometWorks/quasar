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
