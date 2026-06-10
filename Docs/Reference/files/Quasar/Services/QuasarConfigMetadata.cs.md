# Quasar/Services/QuasarConfigMetadata.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Defines the full compile-time schema for every Quasar configuration option that the UI can present and edit. It declares enums for option scope (`Root`/`Session`) and kind (Boolean, Integer, Decimal, Text, LongText, SelectInteger, SelectText), typed record helpers for categories and select options, and a rich `QuasarConfigOptionDefinition` type with validation and full-text search. The static `QuasarConfigMetadata` class holds two read-only lists — `Categories` (12 groupings, including Survival) and `Options` (~150 entries) — and provides reflection-based property lookup and value formatting helpers used by the config editor UI.

## Structure
**Namespace:** `Quasar.Services`

**Types:**
- `QuasarConfigOptionScope` (enum) — `Root`, `Session`
- `QuasarConfigOptionKind` (enum) — `Boolean`, `Integer`, `Decimal`, `Text`, `LongText`, `SelectInteger`, `SelectText`
- `QuasarConfigOptionCategory` (sealed record) — `Key`, `Title`, `Order`, `Description`
- `QuasarConfigSelectOption` (sealed record) — `Value` (int), `Label`, `XmlName`
- `QuasarConfigSelectTextOption` (sealed record) — `Value` (string), `Label`
- `QuasarConfigOptionDefinition` (sealed class) — `Scope`, `PropertyName`, `ElementName`, `CategoryKey`, `Label`, `Kind`, `HelperText`, `Order`, `Min`, `Max`, `Step`, `SelectOptions`, `SelectTextOptions`, `SearchAliases`; `Matches(searchText)` — case-insensitive all-term search across label/helper/property/element/aliases
- `QuasarConfigMetadata` (static class):
  - `Categories` — read-only list of 12 `QuasarConfigOptionCategory` objects, with Survival grouping game mode, production, respawn, oxygen/radiation, hunger, and progression settings
  - `Options` — read-only list of ~150 `QuasarConfigOptionDefinition` objects spanning Root and Session scopes
  - `GetProperty(option)` — returns `PropertyInfo` via reflection on `QuasarWorldRootSettings` or `QuasarSessionSettings`
  - `FormatValue(option, target)` — formats property value as invariant string appropriate for XML serialization
  - Private factory helpers: `RootBool`, `RootInt`, `RootDecimal`, `RootText`, `RootSelectText`, `SessionBool`, `SessionInt`, `SessionDecimal`, `SessionSelect`

## Dependencies
- `Quasar/Models/QuasarWorldRootSettings.cs` (reflected property source for Root options)
- `Quasar/Models/QuasarSessionSettings.cs` (reflected property source for Session options)
- `Quasar/Models/QuasarNetworkType.cs` (used in `FormatValue` for SelectText)

## Notes
- Reflection dictionaries (`RootProperties`, `SessionProperties`) are built once at class initialization and keyed by property name using `StringComparer.Ordinal` for fast lookup.
- `FormatSelectInteger` maps integer enum values to their XML names (e.g. `"Creative"`, `"OFFLINE"`) for direct embedding in `Sandbox_config.sbc` / `SpaceEngineersServer.cfg`.
- One entry deliberately preserves a vanilla typo: `AutoRestatTimeInMin` (note the missing 't') — documented in `HelperText`.
- The Survival category collects settings that are scattered across vanilla Dedicated Server categories (`Players`, `Multipliers`, `Environment`, and `Others`), including respawn ships, oxygen/radiation, hunger, survival buffs, reduced stats on respawn, and backpack despawn.
