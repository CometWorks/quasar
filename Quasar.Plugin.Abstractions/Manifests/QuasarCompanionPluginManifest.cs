using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quasar.Plugin.Abstractions.Manifests;

[JsonConverter(typeof(QuasarCompanionPluginManifestJsonConverter))]
public sealed class QuasarCompanionPluginManifest
{
    public required string Id { get; init; }

    public string? ProjectPath { get; init; }

    public string? EntryAssembly { get; init; }

    public bool IsOwned => !string.IsNullOrWhiteSpace(ProjectPath);
}

public sealed class QuasarCompanionPluginManifestJsonConverter : JsonConverter<QuasarCompanionPluginManifest>
{
    public override QuasarCompanionPluginManifest Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new QuasarCompanionPluginManifest { Id = reader.GetString()?.Trim() ?? string.Empty };

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Companion plugin entries must be strings or objects.");

        string? id = null;
        string? projectPath = null;
        string? entryAssembly = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;

            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Companion plugin object contains an invalid token.");

            var propertyName = reader.GetString();
            reader.Read();

            if (IsProperty(propertyName, "id") || IsProperty(propertyName, "pluginId"))
                id = ReadStringOrNull(ref reader);
            else if (IsProperty(propertyName, "projectPath"))
                projectPath = ReadStringOrNull(ref reader);
            else if (IsProperty(propertyName, "entryAssembly") ||
                     IsProperty(propertyName, "assembly") ||
                     IsProperty(propertyName, "assemblyName"))
                entryAssembly = ReadStringOrNull(ref reader);
            else
                reader.Skip();
        }

        return new QuasarCompanionPluginManifest
        {
            Id = id?.Trim() ?? string.Empty,
            ProjectPath = string.IsNullOrWhiteSpace(projectPath) ? null : projectPath.Trim(),
            EntryAssembly = string.IsNullOrWhiteSpace(entryAssembly) ? null : entryAssembly.Trim(),
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        QuasarCompanionPluginManifest value,
        JsonSerializerOptions options)
    {
        if (!value.IsOwned && string.IsNullOrWhiteSpace(value.EntryAssembly))
        {
            writer.WriteStringValue(value.Id);
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("id", value.Id);
        if (!string.IsNullOrWhiteSpace(value.ProjectPath))
            writer.WriteString("projectPath", value.ProjectPath);
        if (!string.IsNullOrWhiteSpace(value.EntryAssembly))
            writer.WriteString("entryAssembly", value.EntryAssembly);
        writer.WriteEndObject();
    }

    private static bool IsProperty(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static string? ReadStringOrNull(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
}
