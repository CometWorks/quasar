# Quasar/Services/QuasarPluginCatalogService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Maintains the in-memory catalog of available Quasar plugins, sourced from the MagnetarHub GitHub repository (downloaded as a ZIP archive, parsed from XML manifests) and supplemented at runtime by local developer-folder entries. The catalog is cached to disk with a schema-versioned JSON file and can be refreshed on demand. It also exposes helper utilities for URL construction and plugin-id resolution.

## Structure
**Namespace:** `Quasar.Services`

**Type:** `QuasarPluginCatalogService` (sealed class)

Constants:
- `CacheSchemaVersion = 7` — cache is discarded when this changes
- `DotNetCompatPluginId`, `LinuxCompatPluginId` — reserved built-in plugin IDs
- `DefaultHubName/Repo/Branch` — MagnetarHub on GitHub (`main`)
- `DotNetCompatManifestFile`, `LinuxCompatManifestFile` — manifest paths within the hub

| Member | Description |
|---|---|
| `LastRefreshUtc` | UTC timestamp of the last successful remote refresh. |
| `LastError` | Error message from the most recent failed refresh attempt. |
| `GetEntries()` | Returns merged catalog (hub entries + dev-folder entries), sorted by Hidden/FriendlyName/PluginId. Dev-folder entries override hub entries by PluginId. |
| `IsManualSelectionAllowed(pluginId)` (static) | Returns false for `DotNetCompatPluginId` (auto-managed). |
| `GetRepositoryUrl(sourceRepo)` (static) | Converts `"owner/repo"` short form or full HTTPS URL to a canonical GitHub URL. |
| `RefreshAsync(ct)` | Downloads the hub ZIP, parses XML manifests under `/Plugins/`, updates in-memory list and cache. |
| `GetDevFolderPluginId(devFolder)` (static) | Resolves the plugin ID for a dev-folder entry (uses explicit `PluginId` or falls back to source folder name). |

Private:
- `LoadCache()` / `SaveCacheAsync()` — JSON cache at `<QuasarDir>/Caches/plugin-catalog.json`
- `BuildDevFolderEntries()` — reads `PluginManifestReader.ReadMetadata` from each configured dev folder
- Private nested class `QuasarPluginCatalogCache` — wraps `SchemaVersion`, `LastRefreshUtc`, `Entries`

## Dependencies
- [`Quasar/Services/QuasarDevFolderCatalog.cs`](QuasarDevFolderCatalog.cs.md)
- `Quasar/Models/QuasarPluginCatalogEntry.cs`
- `Quasar/Models/QuasarDevFolderSelection.cs`
- `Magnetar.Protocol.Runtime.MagnetarPaths`
- `Magnetar.Protocol.Runtime.AtomicFileWriter`
- `Magnetar.Protocol.Runtime.PluginManifestReader` (reads dev-folder XML manifests)
- `System.IO.Compression.ZipArchive` (hub archive parsing)
- `System.Xml.Linq` (XML manifest parsing)
- `IHttpClientFactory` (HTTP download)

## Notes
- Thread safety: `_entries` list is guarded by `_sync`.
- The ZIP archive is streamed directly without buffering to disk.
- Dev-folder entries always win over hub entries for the same plugin ID, enabling local override during development.
- `DotNetCompatPluginId` is excluded from manual selection because Quasar manages it automatically.
