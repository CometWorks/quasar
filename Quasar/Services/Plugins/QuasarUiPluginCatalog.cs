using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Quasar.Plugin.Abstractions;
using Quasar.Plugin.Abstractions.Extensions;
using Quasar.Plugin.Abstractions.Navigation;

namespace Quasar.Services.Plugins;

public sealed class QuasarUiPluginCatalog
{
    public const string StaticAssetRequestPathPrefix = "/_quasar/plugins";

    private readonly IReadOnlyList<QuasarLoadedUiPlugin> _plugins;
    private readonly List<string> _loadErrors;

    private QuasarUiPluginCatalog(
        IReadOnlyList<QuasarLoadedUiPlugin> plugins,
        bool safeMode,
        IReadOnlyList<string> safeModeReasons,
        IEnumerable<string>? loadErrors = null)
    {
        _plugins = plugins;
        _loadErrors = loadErrors?.ToList() ?? [];
        SafeMode = safeMode;
        SafeModeReasons = safeModeReasons;
        RazorAssemblies = plugins
            .SelectMany(plugin => plugin.Plugin.GetRazorAssemblies())
            .Distinct()
            .ToArray();
        NavItems = plugins
            .SelectMany(plugin => plugin.Plugin.GetNavItems())
            .OrderBy(item => item.Zone, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Order)
            .ThenBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Extensions = plugins
            .SelectMany(plugin => plugin.Plugin.GetExtensions())
            .OrderBy(item => item.TargetKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Priority)
            .ToArray();
    }

    public bool SafeMode { get; }

    public IReadOnlyList<string> SafeModeReasons { get; }

    public IReadOnlyList<string> LoadErrors => _loadErrors;

    public IReadOnlyList<QuasarLoadedUiPlugin> LoadedPlugins => _plugins;

    public IReadOnlyList<Assembly> RazorAssemblies { get; }

    public IReadOnlyList<QuasarNavItem> NavItems { get; }

    public IReadOnlyList<QuasarExtensionContribution> Extensions { get; }

    public static QuasarUiPluginCatalog Create(IConfiguration configuration, IHostEnvironment environment)
    {
        var safeMode = IsSafeModeEnabled(configuration, out var safeModeReasons);
        if (safeMode)
            return new QuasarUiPluginCatalog([], safeMode: true, safeModeReasons);

        var loadErrors = new List<string>();
        var plugins = new List<QuasarLoadedUiPlugin>();
        foreach (var manifestDirectory in DiscoverManifestDirectories(configuration))
        {
            try
            {
                var loadedPlugin = LoadPlugin(configuration, environment, manifestDirectory);
                plugins.Add(loadedPlugin);
            }
            catch (Exception exception)
            {
                loadErrors.Add($"{manifestDirectory}: {exception.Message}");
            }
        }

        return new QuasarUiPluginCatalog(plugins, safeMode: false, safeModeReasons: [], loadErrors);
    }

    public void ConfigurePluginServices(IServiceCollection services)
    {
        foreach (var loadedPlugin in _plugins)
            loadedPlugin.Plugin.ConfigureServices(services, loadedPlugin.Context);
    }

    public void ConfigurePluginEndpoints(IEndpointRouteBuilder endpoints)
    {
        foreach (var loadedPlugin in _plugins)
            loadedPlugin.Plugin.ConfigureEndpoints(endpoints, loadedPlugin.Context);
    }

    public void UsePluginStaticAssets(IApplicationBuilder app)
    {
        foreach (var loadedPlugin in _plugins)
        {
            var staticAssetsDirectory = loadedPlugin.Context.StaticAssetsDirectory;
            if (string.IsNullOrWhiteSpace(staticAssetsDirectory) ||
                !Directory.Exists(staticAssetsDirectory))
            {
                continue;
            }

            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(staticAssetsDirectory),
                RequestPath = $"{StaticAssetRequestPathPrefix}/{loadedPlugin.Id}",
            });
        }
    }

    private static bool IsSafeModeEnabled(IConfiguration configuration, out IReadOnlyList<string> reasons)
    {
        var values = new List<string>();

        if (configuration.GetValue<bool>("safe-mode"))
            values.Add("command-line safe-mode");

        if (configuration.GetValue<bool>("Quasar:Plugins:SafeMode"))
            values.Add("configuration Quasar:Plugins:SafeMode");

        if (IsTruthy(Environment.GetEnvironmentVariable("QUASAR_SAFE_MODE")))
            values.Add("environment QUASAR_SAFE_MODE");

        if (IsTruthy(Environment.GetEnvironmentVariable("QUASAR_DISABLE_UI_PLUGINS")))
            values.Add("environment QUASAR_DISABLE_UI_PLUGINS");

        var markerPath = GetSafeModeMarkerPath();
        if (File.Exists(markerPath))
            values.Add($"marker {markerPath}");

        reasons = values;
        return values.Count > 0;
    }

    private static string GetSafeModeMarkerPath() =>
        Path.Combine(MagnetarPaths.GetQuasarDirectory(), "ui-plugins.safe-mode");

    private static bool IsTruthy(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> DiscoverManifestDirectories(IConfiguration configuration)
    {
        foreach (var root in ResolvePluginRoots(configuration))
        {
            if (!Directory.Exists(root))
                continue;

            if (File.Exists(Path.Combine(root, QuasarPluginPackageManifestReader.ManifestFileName)))
            {
                yield return root;
                continue;
            }

            foreach (var child in Directory.EnumerateDirectories(root))
            {
                if (File.Exists(Path.Combine(child, QuasarPluginPackageManifestReader.ManifestFileName)))
                    yield return child;
            }
        }
    }

    private static IEnumerable<string> ResolvePluginRoots(IConfiguration configuration)
    {
        var configuredRoots = Environment.GetEnvironmentVariable("QUASAR_UI_PLUGIN_DIRS")
                              ?? Environment.GetEnvironmentVariable("QUASAR_UI_PLUGIN_DIR")
                              ?? configuration["Quasar:Plugins:Directory"];

        if (string.IsNullOrWhiteSpace(configuredRoots))
        {
            yield return Path.Combine(MagnetarPaths.GetQuasarDirectory(), "Plugins");
            yield break;
        }

        foreach (var root in configuredRoots.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return Path.GetFullPath(root);
    }

    private static QuasarLoadedUiPlugin LoadPlugin(
        IConfiguration configuration,
        IHostEnvironment environment,
        string manifestDirectory)
    {
        var manifestPath = Path.Combine(manifestDirectory, QuasarPluginPackageManifestReader.ManifestFileName);
        var manifest = QuasarPluginPackageManifestReader.Read(manifestPath);
        var entryAssemblyPath = ResolveEntryAssemblyPath(configuration, environment, manifestDirectory, manifest.EntryAssembly);
        var shadowDirectory = ShadowCopyPlugin(manifest.Id, entryAssemblyPath);
        var shadowEntryAssemblyPath = Path.Combine(shadowDirectory, Path.GetFileName(entryAssemblyPath));
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(shadowEntryAssemblyPath);
        var entryType = assembly.GetType(manifest.EntryType, throwOnError: true)
                        ?? throw new InvalidOperationException($"Plugin entry type not found: {manifest.EntryType}");

        if (!typeof(IQuasarPlugin).IsAssignableFrom(entryType))
            throw new InvalidOperationException($"Plugin entry type does not implement {nameof(IQuasarPlugin)}: {manifest.EntryType}");

        var plugin = (IQuasarPlugin?)Activator.CreateInstance(entryType)
                     ?? throw new InvalidOperationException($"Plugin entry type could not be created: {manifest.EntryType}");

        var staticAssetsDirectory = ResolveOptionalDirectory(manifestDirectory, manifest.StaticAssets);
        var context = new QuasarPluginContext
        {
            Manifest = manifest,
            PluginDirectory = manifestDirectory,
            CacheDirectory = shadowDirectory,
            StaticAssetsDirectory = staticAssetsDirectory,
            Configuration = configuration,
            Environment = environment,
        };

        return new QuasarLoadedUiPlugin(plugin, context);
    }

    private static string ResolveEntryAssemblyPath(
        IConfiguration configuration,
        IHostEnvironment environment,
        string manifestDirectory,
        string entryAssembly)
    {
        var directPath = Path.Combine(manifestDirectory, entryAssembly);
        if (File.Exists(directPath))
            return Path.GetFullPath(directPath);

        var buildConfiguration = Environment.GetEnvironmentVariable("QUASAR_UI_PLUGIN_BUILD_CONFIGURATION")
                                 ?? configuration["Quasar:Plugins:BuildConfiguration"]
                                 ?? (environment.IsDevelopment() ? "Debug" : "Release");

        var candidates = Directory.EnumerateFiles(manifestDirectory, entryAssembly, SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                           path.Contains($"{Path.AltDirectorySeparatorChar}bin{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderByDescending(path => path.Contains($"{Path.DirectorySeparatorChar}{buildConfiguration}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path.Length)
            .ToList();

        return candidates.FirstOrDefault()
               ?? throw new FileNotFoundException($"Quasar plugin entry assembly was not found: {entryAssembly}", entryAssembly);
    }

    private static string? ResolveOptionalDirectory(string baseDirectory, string? relativeOrAbsolutePath)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
            return null;

        var path = Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(baseDirectory, relativeOrAbsolutePath);

        return Directory.Exists(path)
            ? Path.GetFullPath(path)
            : null;
    }

    private static string ShadowCopyPlugin(string pluginId, string entryAssemblyPath)
    {
        var assemblyDirectory = Path.GetDirectoryName(entryAssemblyPath)
                                ?? throw new InvalidOperationException($"Entry assembly has no directory: {entryAssemblyPath}");
        var hash = ComputeShortHash(entryAssemblyPath);
        var pluginCacheDirectory = Path.Combine(
            MagnetarPaths.GetQuasarDirectory(),
            "Caches",
            "ui-plugins",
            SanitizePathSegment(pluginId),
            hash);

        Directory.CreateDirectory(pluginCacheDirectory);
        CopyDirectory(assemblyDirectory, pluginCacheDirectory);
        return pluginCacheDirectory;
    }

    private static string ComputeShortHash(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory))
        {
            var destinationFile = Path.Combine(destinationDirectory, Path.GetFileName(sourceFile));
            File.Copy(sourceFile, destinationFile, overwrite: true);
        }

        foreach (var sourceChildDirectory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var destinationChildDirectory = Path.Combine(destinationDirectory, Path.GetFileName(sourceChildDirectory));
            Directory.CreateDirectory(destinationChildDirectory);
            CopyDirectory(sourceChildDirectory, destinationChildDirectory);
        }
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized)
            ? "plugin"
            : sanitized;
    }
}
