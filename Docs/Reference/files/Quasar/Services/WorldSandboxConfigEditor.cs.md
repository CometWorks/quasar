# Quasar/Services/WorldSandboxConfigEditor.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Static editor for the Space Engineers `Sandbox_config.sbc` XML file. Provides operations for reading the current mod list from the `<Mods>` element and atomically applying a `QuasarConfigProfile` by upserting `<Settings>` values, writing `BlockTypeLimits` in the vanilla dictionary shape, plus rewriting `<Mods>`. XML whitespace is preserved on read; output is normalized to LF line endings and UTF-8 without BOM.

## Structure
**Namespace:** `Quasar.Services`

**Type:** `WorldSandboxConfigEditor` (static class)

| Member | Description |
|---|---|
| `SandboxConfigFileName` (const) | `"Sandbox_config.sbc"` |
| `ReadMods(sandboxConfigPath)` | Parses `<Mods>/<ModItem>` elements; extracts `PublishedFileId` (long) and `FriendlyName` attribute; deduplicates by workshopId; returns empty list if file or `<Mods>` element absent. |
| `WriteModsAsync(sandboxConfigPath, mods, ct)` | Compatibility path that rewrites only the mod list without touching session settings. |
| `WriteProfileAsync(sandboxConfigPath, profile, ct)` | Loads existing XML, creates `<Settings>` when absent, upserts every session option from `QuasarConfigMetadata`, replaces or creates the `<Mods>` element, then writes atomically. Throws `FileNotFoundException` if the file does not exist. |

Private:
- `ApplySessionSettings(root, sessionSettings)` — writes profile session values using exact DS XML element names
- `UpsertBlockTypeLimits(settingsElement, limits)` — writes `<BlockTypeLimits><dictionary><item><Key>...</Key><Value>...</Value></item></dictionary></BlockTypeLimits>`
- `ApplyMods(root, mods)` — replaces mod entries with profile mods
- `LoadDocument(sandboxConfigPath)` / `GetRoot(document, sandboxConfigPath)` — shared XML loading and validation helpers
- `UpsertElement(parent, name, value)` — creates or updates one XML element
- `SerializeXml(document)` — `XmlWriter` with `Indent=true`, UTF-8 no-BOM, no XML declaration omission, `\n` line endings
- `Utf8StringWriter` (private nested class) — overrides `Encoding` to UTF-8 no-BOM

Each written `<ModItem>` contains:
- Attribute: `FriendlyName`
- Child elements: `<Name>` (`{id}.sbm`), `<PublishedFileId>`, `<PublishedServiceName>` (`Steam`)

## Dependencies
- `Quasar/Models/QuasarModSelection.cs`
- [`Quasar/Models/QuasarConfigProfile.cs`](../Models/QuasarConfigProfile.cs.md)
- [`Quasar/Services/QuasarConfigMetadata.cs`](QuasarConfigMetadata.cs.md)
- `Magnetar.Protocol.Runtime.AtomicFileWriter` (safe write)
- `System.Xml.Linq` / `System.Xml.XmlWriter`

## Notes
- `XDocument.Load` uses `LoadOptions.PreserveWhitespace` on read to avoid mangling the rest of the file; `SerializeXml` re-normalizes indentation on write.
- The XML declaration (`<?xml version="1.0" encoding="utf-8"?>`) is always emitted (OmitXmlDeclaration = false).
- `<Mods>` is inserted after `<Settings>` if it does not already exist, matching the game's expected element order.
