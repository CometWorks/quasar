using System.Xml.Linq;

namespace Quasar.Services;

/// <summary>
/// Reads a local plugin's manifest XML and extracts its declared plugin id
/// (the top-level <c>&lt;Id&gt;</c> element). Used to populate
/// <see cref="Quasar.Models.QuasarDevFolderSelection.PluginId"/> when an admin
/// registers a dev folder.
/// </summary>
/// <remarks>
/// Magnetar identifies a dev-folder plugin by the manifest's own
/// <c>&lt;Id&gt;</c> (a GUID for typical plugins, e.g.
/// <c>D32C082B-1A80-418A-BB6B-28E83D86F940</c>), independent of the folder name.
/// The active profile's <c>&lt;LocalFolderConfig&gt;&lt;Id&gt;</c> and the
/// <c>&lt;LocalPlugin&gt;&lt;Name&gt;</c> source entry must carry that id for the
/// dev folder to be matched, enabled and compiled, so we read it here. The
/// manifest (the profile's <c>&lt;DataFile&gt;</c>) is also what Magnetar reads to
/// discover the source directories to compile and any dependencies.
/// </remarks>
public static class PluginManifestReader
{
    /// <summary>
    /// Loads the manifest at <paramref name="manifestPath"/> and returns its
    /// plugin <c>&lt;Id&gt;</c> value.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown with a user-friendly message when the file is missing, cannot be
    /// parsed as XML, or does not contain an <c>&lt;Id&gt;</c> element.
    /// </exception>
    public static string ReadPluginId(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new InvalidOperationException("Manifest path is empty.");

        if (!File.Exists(manifestPath))
            throw new InvalidOperationException($"Plugin manifest not found: {manifestPath}");

        XDocument document;
        try
        {
            document = XDocument.Load(manifestPath);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Plugin manifest could not be parsed: {manifestPath} ({exception.Message})");
        }

        var id = document.Descendants("Id").FirstOrDefault()?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException($"Plugin manifest has no <Id> element: {manifestPath}");

        return id;
    }
}
