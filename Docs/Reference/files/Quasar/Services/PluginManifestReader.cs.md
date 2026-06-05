# Quasar/Services/PluginManifestReader.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
`PluginManifestReader` is a static utility for validating and reading metadata from a Magnetar plugin's manifest XML file when an admin registers a dev folder. It checks file existence and XML well-formedness, and extracts display fields (`FriendlyName`, `Author`, `Description`, `Tooltip`, `Runtimes`).

## Structure
Namespace: `Quasar.Services`

**`PluginManifestReader`** — `static class`

| Member | Notes |
|--------|-------|
| `ValidateManifest(string manifestPath)` | Throws `InvalidOperationException` with a user-friendly message if the file is missing or not valid XML |
| `ReadMetadata(string manifestPath)` | Returns a `PluginManifestMetadata` record; returns defaults on missing file or parse failure |

**`PluginManifestMetadata`** — `sealed record(FriendlyName, Author, Description, Tooltip, Runtimes)` — all default to `""`.

## Dependencies
- BCL `System.Xml.Linq.XDocument`

## Notes
Magnetar identifies dev-folder plugins by the source folder name (e.g. `se-test-plugin`), not by the manifest's `<Id>` element (which is a GUID for typical plugins). The folder name becomes the `<LocalPlugin><Name>` and `<LocalFolderConfig><Id>` in the generated sources/profile XML. See `QuasarDevFolderSelection.SourceFolderName`.
