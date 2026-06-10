# Quasar/Models/QuasarConfigProfile.cs

**Module:** Quasar.Models  **Kind:** class  **Tier:** 1

## Summary
Defines the configuration profile model for Space Engineers dedicated server instances managed by Quasar. A profile bundles world root settings, session settings, plugin selections, and mod selections that can be applied to one or more instances. Also defines the `QuasarNetworkType` enum with its custom JSON converter, and supporting sub-models for plugins, mods, catalog entries, and dev-folder selections.

## Structure
Namespace: `Quasar.Models`

**`QuasarNetworkType`** — enum; `Steam` | `EOS`. Serializes to `"steam"` / `"EOS"` via `QuasarNetworkTypeJsonConverter`.

**`QuasarNetworkTypeJsonConverter`** — sealed `JsonConverter<QuasarNetworkType>`; case-insensitive read, writes lowercase `"steam"` / uppercase `"EOS"`.

**`QuasarNetworkTypeExtensions`** — static; `ToConfigValue()` extension.

**`QuasarConfigProfile`** — sealed class; the top-level profile entity:
- `ConfigProfileId` (GUID string, auto-generated)
- `Name`, `Description`
- `RootSettings` — `QuasarWorldRootSettings`
- `SessionSettings` — `QuasarSessionSettings`
- `Plugins` — `List<QuasarPluginSelection>`
- `Mods` — `List<QuasarModSelection>`
- `UpdatedAtUtc`

**`QuasarDevFolderSelection`** — sealed class; describes a local development plugin folder:
- `Name`, `FolderPath`, `DataFile`, `PluginId`, `DebugBuild`
- `SourceFolderName` (`[JsonIgnore]`) — computed innermost folder name; used as Magnetar `LocalPlugin.Name` and `LocalFolderConfig.Id`
- `static GetSourceFolderName(string)` — path utility

**`QuasarWorldRootSettings`** — sealed class; ~40 properties mirroring Magnetar/DS root config XML: network type, asteroid count, MOTD, auto-restart, auto-update, watcher interval, admin/reserved/banned lists, chat anti-spam, console compatibility, etc.

**`QuasarSessionSettings`** — sealed class; ~100 properties covering game-mode, player limits, PCU, multipliers, survival mechanics (oxygen/radiation, hunger, respawn, backpacks, buffs), gameplay toggles, economy, trash removal, and more — mirrors the SE `MyObjectBuilder_SessionSettings` fields.

**`QuasarPluginSelection`** — sealed class; `PluginId`, `DisplayName`, `SelectedVersion`.

**`QuasarModSelection`** — sealed class; `WorkshopId` (long), `DisplayName`.

**`QuasarPluginCatalogEntry`** — sealed class; metadata for a plugin in the catalog: `PluginId`, `FriendlyName`, `Author`, `Description`, `Tooltip`, `Runtimes`, `SourceRepo`, `ManifestRepo`, `ManifestBranch`, `ManifestFile`, `Hidden`, `IsLocalDevFolder`.

## Dependencies
- `System.Text.Json`, `System.Text.Json.Serialization`
- [`Quasar/Services/QuasarConfigProfileCatalog.cs`](../Services/QuasarConfigProfileCatalog.cs.md) (owns and persists `QuasarConfigProfile` instances)
- [`Quasar/Services/QuasarDevFolderCatalog.cs`](../Services/QuasarDevFolderCatalog.cs.md) (owns `QuasarDevFolderSelection` instances)
- [`Quasar/Services/QuasarPluginCatalogService.cs`](../Services/QuasarPluginCatalogService.cs.md) (uses `QuasarPluginCatalogEntry`)

## Notes
`QuasarWorldRootSettings.NetworkType` uses `QuasarNetworkType` which is serialized as a plain string; the custom converter ensures backward-compatible reading even with mixed case. `SourceFolderName` is intentionally `[JsonIgnore]` — it is derived at runtime and must not round-trip through JSON.
