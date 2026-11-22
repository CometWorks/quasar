using System.Text.Json;
using System.Text.Json.Nodes;

namespace Quasar.Services.PluginSdk;

/// <summary>
/// Quasar-side POCOs mirroring the <c>ConfigStorage.SaveJson</c> envelope and
/// the <c>ConfigSchema</c> document so the editor can render a UI from a
/// plugin's schema without referencing Magnetar's PluginSdk. Field names match
/// the SDK's camelCase JSON; System.Text.Json (Web defaults) binds them
/// case-insensitively.
/// </summary>
public sealed class PluginConfigEnvelope
{
    public ConfigSchemaDto Schema { get; set; } = new();

    /// <summary>All options at their default values (raw JSON).</summary>
    public JsonElement Defaults { get; set; }

    /// <summary>All options at their current values (raw JSON).</summary>
    public JsonElement Values { get; set; }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Parses a <c>SaveJson</c> envelope string. Returns <c>null</c> if the
    /// document is empty or cannot be parsed.
    /// </summary>
    public static PluginConfigEnvelope? Parse(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return null;

        try
        {
            return JsonSerializer.Deserialize<PluginConfigEnvelope>(configJson, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the <c>values</c> section as a mutable <see cref="JsonObject"/>
    /// the editor can edit in place, or an empty object when absent.
    /// </summary>
    public JsonObject CloneValues()
    {
        if (Values.ValueKind != JsonValueKind.Object)
            return new JsonObject();

        return JsonNode.Parse(Values.GetRawText()) as JsonObject ?? new JsonObject();
    }

    /// <summary>
    /// Returns the <c>defaults</c> section as a mutable <see cref="JsonObject"/>
    /// the editor can use for reset-to-defaults.
    /// </summary>
    public JsonObject CloneDefaults()
    {
        if (Defaults.ValueKind != JsonValueKind.Object)
            return new JsonObject();

        return JsonNode.Parse(Defaults.GetRawText()) as JsonObject ?? new JsonObject();
    }
}

public sealed class ConfigSchemaDto
{
    public List<LayoutContainerDto> Layout { get; set; } = new();
    public List<ConfigPropertyDto> Properties { get; set; } = new();
    public Dictionary<string, StructDto> Structs { get; set; } = new();
    public Dictionary<string, List<EnumValueDto>> Enums { get; set; } = new();
}

/// <summary>One host of the layout tree (tab / section / group).</summary>
public sealed class LayoutContainerDto
{
    public string? Kind { get; set; }
    public string? Id { get; set; }
    public string? Parent { get; set; }
    public string? Caption { get; set; }
}

/// <summary>
/// Metadata for one config option. Type-specific fields are null when they do
/// not apply. <see cref="Type"/> is one of: bool, int, long, float, double,
/// string, enum, list, dict, struct, Color, Vector2D, Vector3D, Vector2I,
/// Vector3I, Direction, MyPositionAndOrientation.
/// </summary>
public sealed class ConfigPropertyDto
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Parent { get; set; }

    public double? Min { get; set; }
    public double? Max { get; set; }

    public int? MaxLength { get; set; }
    public string? Pattern { get; set; }
    public bool? Multiline { get; set; }

    public int? MaxCount { get; set; }
    public string? ElementType { get; set; }
    public string? ElementStruct { get; set; }
    public string? ElementEnum { get; set; }
    public string? KeyType { get; set; }
    public string? ValueType { get; set; }
    public string? ValueStruct { get; set; }
    public string? ValueEnum { get; set; }
    public string? TreeParentField { get; set; }

    public string? StructName { get; set; }
    public string? EnumName { get; set; }

    public bool? HasAlpha { get; set; }

    /// <summary>The camelCase key under which this option appears in the values document.</summary>
    public string JsonKey => string.IsNullOrEmpty(Name)
        ? Name
        : char.ToLowerInvariant(Name[0]) + Name.Substring(1);
}

public sealed class StructDto
{
    public List<StructMemberDto> Members { get; set; } = new();
    public string? CaptionMember { get; set; }
}

public sealed class StructMemberDto
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ElementType { get; set; }
    public string? ElementStruct { get; set; }
    public string? ElementEnum { get; set; }
    public string? KeyType { get; set; }
    public string? ValueType { get; set; }
    public string? ValueStruct { get; set; }
    public string? ValueEnum { get; set; }
    public string? StructName { get; set; }
    public string? EnumName { get; set; }
}

public sealed class EnumValueDto
{
    public string Name { get; set; } = string.Empty;
    public string Caption { get; set; } = string.Empty;
}
