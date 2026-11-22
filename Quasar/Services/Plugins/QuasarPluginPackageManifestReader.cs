using System.Text.Json;
using System.Text.Json.Serialization;
using Quasar.Plugin.Abstractions.Manifests;

namespace Quasar.Services.Plugins;

public static class QuasarPluginPackageManifestReader
{
    public const string ManifestFileName = "quasar-plugin.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static QuasarPluginManifest Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Quasar plugin manifest path is empty.");

        if (!File.Exists(path))
            throw new FileNotFoundException("Quasar plugin manifest was not found.", path);

        var json = File.ReadAllText(path);
        var manifest = JsonSerializer.Deserialize<QuasarPluginManifest>(json, JsonOptions)
                       ?? throw new InvalidOperationException($"Quasar plugin manifest is empty: {path}");

        Validate(manifest, path);
        return manifest;
    }

    private static void Validate(QuasarPluginManifest manifest, string path)
    {
        Require(path, manifest.Id, "id");
        Require(path, manifest.DisplayName, "displayName");
        Require(path, manifest.Version, "version");
        Require(path, manifest.EntryAssembly, "entryAssembly");
        Require(path, manifest.EntryType, "entryType");
        Require(path, manifest.ProjectPath, "projectPath");

        if (manifest.BrowserSdkVersion < 0 || manifest.BrowserSdkVersion > Quasar.Plugin.Abstractions.Operations.QuasarSdkLimits.ProtocolVersion)
            throw new InvalidOperationException($"Plugin '{manifest.Id}' requires browser SDK v{manifest.BrowserSdkVersion}. Update Quasar and the plugin together.");

        if (manifest.BrowserAssembly is { } browser &&
            (!System.Text.RegularExpressions.Regex.IsMatch(browser, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]*\.dll\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
             || string.Equals(browser, manifest.EntryAssembly, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Plugin '{manifest.Id}' must declare a separate browserAssembly DLL by filename.");

        foreach (var companion in manifest.CompanionPluginManifests)
        {
            Require(path, companion.Id, "companionPlugins[].id");
            if (companion.IsOwned)
                Require(path, companion.ProjectPath, "companionPlugins[].projectPath");
        }
    }

    private static void Require(string path, string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Quasar plugin manifest {path} is missing '{propertyName}'.");
    }
}
