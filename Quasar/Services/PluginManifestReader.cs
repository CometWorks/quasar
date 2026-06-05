using System.Xml.Linq;

namespace Quasar.Services;

/// <summary>
/// Validates a local plugin's manifest XML (the profile's <c>&lt;DataFile&gt;</c>)
/// when an admin registers a dev folder. The manifest is what Magnetar reads to
/// discover the source directories to compile and any dependencies.
/// </summary>
/// <remarks>
/// Magnetar identifies a dev-folder plugin by the <em>source folder name</em>
/// (e.g. <c>se-test-plugin</c>), not by the manifest's own <c>&lt;Id&gt;</c>
/// (a GUID for typical plugins). That folder name is carried into the active
/// profile's <c>&lt;LocalFolderConfig&gt;&lt;Id&gt;</c> and the
/// <c>&lt;LocalPlugin&gt;&lt;Name&gt;</c> source entry. See
/// <see cref="Quasar.Models.QuasarDevFolderSelection.SourceFolderName"/>.
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
}
