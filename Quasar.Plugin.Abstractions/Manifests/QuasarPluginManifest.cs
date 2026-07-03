using System.Text.Json.Serialization;

namespace Quasar.Plugin.Abstractions.Manifests;

public sealed class QuasarPluginManifest
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string Version { get; init; }

    public required string EntryAssembly { get; init; }

    public required string EntryType { get; init; }

    public required string ProjectPath { get; init; }

    public string? StaticAssets { get; init; }

    public IReadOnlyList<string> Stylesheets { get; init; } = [];

    public string? QuasarVersion { get; init; }

    [JsonPropertyName("companionPlugins")]
    public IReadOnlyList<QuasarCompanionPluginManifest> CompanionPluginManifests
    {
        get => _companionPluginManifests;
        init => _companionPluginManifests = value ?? [];
    }

    [JsonIgnore]
    [Obsolete("Use CompanionPluginManifests for owned companion plugin metadata.")]
    public IReadOnlyList<string> CompanionPlugins
    {
        get => _companionPluginManifests.Select(companion => companion.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToArray();
        init => _companionPluginManifests = value?.Select(id => new QuasarCompanionPluginManifest { Id = id }).ToArray() ?? [];
    }

    public IReadOnlyList<string> Dependencies { get; init; } = [];

    private IReadOnlyList<QuasarCompanionPluginManifest> _companionPluginManifests = [];
}
