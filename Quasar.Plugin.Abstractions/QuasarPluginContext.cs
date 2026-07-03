using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Quasar.Plugin.Abstractions.Manifests;

namespace Quasar.Plugin.Abstractions;

public sealed class QuasarPluginContext
{
    public required QuasarPluginManifest Manifest { get; init; }

    public required string PluginDirectory { get; init; }

    public required string CacheDirectory { get; init; }

    public string? StaticAssetsDirectory { get; init; }

    public required IConfiguration Configuration { get; init; }

    public required IHostEnvironment Environment { get; init; }

    public string PluginId => Manifest.Id;
}
