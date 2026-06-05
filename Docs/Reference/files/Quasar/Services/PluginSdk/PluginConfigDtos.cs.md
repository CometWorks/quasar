# Quasar/Services/PluginSdk/PluginConfigDtos.cs

**Module:** Quasar.Services.PluginSdk  **Kind:** class  **Tier:** 2

## Summary

Quasar-side POCOs that mirror the `ConfigStorage.SaveJson` envelope and `ConfigSchema` document produced by Magnetar's PluginSdk. These DTOs allow the Blazor config editor to deserialise and render a plugin's schema-driven UI without taking a direct dependency on the PluginSdk assembly. Field names match the SDK's camelCase JSON and are bound case-insensitively by `System.Text.Json` (Web defaults).

## Structure

Namespace: `Quasar.Services.PluginSdk`

**`PluginConfigEnvelope`** (sealed class) — top-level envelope
- `Schema : ConfigSchemaDto` — the schema that describes available options
- `Defaults : JsonElement` — raw JSON object of all options at default values
- `Values : JsonElement` — raw JSON object of all options at current values
- `Parse(string?) : PluginConfigEnvelope?` (static) — deserialises the JSON string; returns `null` on empty or `JsonException`
- `CloneValues() : JsonObject` — deep-clones the values section for in-place editing
- `CloneDefaults() : JsonObject` — deep-clones the defaults section for reset-to-defaults

**`ConfigSchemaDto`** (sealed class) — schema root
- `Layout : List<LayoutContainerDto>` — tab/section/group tree nodes
- `Properties : List<ConfigPropertyDto>` — flat list of all config options
- `Structs : Dictionary<string, StructDto>` — named struct definitions
- `Enums : Dictionary<string, List<EnumValueDto>>` — named enum definitions

**`LayoutContainerDto`** (sealed class) — one layout tree node
- `Kind`, `Id`, `Parent`, `Caption`

**`ConfigPropertyDto`** (sealed class) — one config option descriptor
- `Name`, `Type`, `Description`, `Parent`
- Numeric constraints: `Min`, `Max`
- String constraints: `MaxLength`, `Pattern`
- Collection metadata: `MaxCount`, `ElementType`, `ElementStruct`, `ElementEnum`, `KeyType`, `ValueType`, `ValueStruct`, `ValueEnum`, `TreeParentField`
- Type references: `StructName`, `EnumName`
- Color-specific: `HasAlpha`
- Computed: `JsonKey` — camelCase version of `Name`

**`StructDto`** (sealed class) — struct type definition
- `Members : List<StructMemberDto>`, `CaptionMember`

**`StructMemberDto`** (sealed class) — one struct field
- `Name`, `Type`, `Description`, plus same collection/type-ref fields as `ConfigPropertyDto`

**`EnumValueDto`** (sealed class)
- `Name`, `Caption`

## Dependencies

- BCL: `System.Text.Json`, `System.Text.Json.Nodes`

## Notes

Supported `Type` values (per doc comment): `bool`, `int`, `long`, `float`, `double`, `string`, `enum`, `list`, `dict`, `struct`, `Color`, `Vector2D`, `Vector3D`, `Vector2I`, `Vector3I`, `Direction`, `MyPositionAndOrientation`.
