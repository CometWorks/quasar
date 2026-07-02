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

    public string? QuasarVersion { get; init; }

    public IReadOnlyList<string> CompanionPlugins { get; init; } = [];

    public IReadOnlyList<string> Dependencies { get; init; } = [];
}
