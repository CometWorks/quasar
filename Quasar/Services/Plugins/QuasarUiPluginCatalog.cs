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
using Quasar.Plugin.Abstractions.Manifests;
using Quasar.Plugin.Abstractions.Navigation;

namespace Quasar.Services.Plugins;

public sealed class QuasarUiPluginCatalog
{
    public const string StaticAssetRequestPathPrefix = "/_quasar/plugins";

    private static readonly object AssemblyResolverSync = new();
    private static readonly Dictionary<string, List<PluginAssemblyCandidate>> PluginAssemblyCandidates = new(StringComparer.OrdinalIgnoreCase);
    private static bool _assemblyResolverRegistered;

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
        RazorAssemblies = CollectPluginValues(plugins, plugin => plugin.Plugin.GetRazorAssemblies(), "Razor assemblies")
            .Distinct()
            .ToArray();
        NavItems = CollectPluginValues(plugins, plugin => plugin.Plugin.GetNavItems(), "nav items")
            .OrderBy(item => item.Zone, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Order)
            .ThenBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Extensions = CollectPluginValues(plugins, plugin => plugin.Plugin.GetExtensions(), "extensions")
            .OrderBy(item => item.TargetKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Priority)
            .ToArray();
        StylesheetHrefs = plugins
            .SelectMany(GetStylesheetHrefs)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<T> CollectPluginValues<T>(
        IReadOnlyList<QuasarLoadedUiPlugin> plugins,
        Func<QuasarLoadedUiPlugin, IEnumerable<T>> selector,
        string label)
    {
        var values = new List<T>();
        foreach (var plugin in plugins)
        {
            try
            {
                values.AddRange(selector(plugin));
            }
            catch (Exception exception)
            {
                _loadErrors.Add($"{plugin.Context.PluginDirectory}: failed to get {label}: {exception.Message}");
            }
        }

        return values;
    }

    public bool SafeMode { get; }

    public IReadOnlyList<string> SafeModeReasons { get; }

    public IReadOnlyList<string> LoadErrors => _loadErrors;

    public IReadOnlyList<QuasarLoadedUiPlugin> LoadedPlugins => _plugins;

    public IReadOnlyList<Assembly> RazorAssemblies { get; }

    public IReadOnlyList<QuasarNavItem> NavItems { get; }

    public IReadOnlyList<QuasarExtensionContribution> Extensions { get; }

    public IReadOnlyList<string> StylesheetHrefs { get; }

    public static string SafeModeMarkerPath => GetSafeModeMarkerPath();

    public static QuasarUiPluginCatalog Create(IConfiguration configuration, IHostEnvironment environment)
    {
        var safeMode = IsSafeModeEnabled(configuration, out var safeModeReasons);
        if (safeMode)
            return new QuasarUiPluginCatalog([], safeMode: true, safeModeReasons);

        var loadErrors = new List<string>();
        var plugins = new List<QuasarLoadedUiPlugin>();
        var pluginStates = QuasarUiPluginStateStore.LoadSnapshot();
        foreach (var manifestDirectory in DiscoverManifestDirectories(configuration))
        {
            try
            {
                var manifestPath = Path.Combine(manifestDirectory, QuasarPluginPackageManifestReader.ManifestFileName);
                var manifest = QuasarPluginPackageManifestReader.Read(manifestPath);
                if (!QuasarUiPluginStateStore.IsEnabled(pluginStates, manifest.Id))
                    continue;

                var loadedPlugin = LoadPlugin(configuration, environment, manifestDirectory, manifest);
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
        string manifestDirectory,
        QuasarPluginManifest manifest)
    {
        var entryAssemblyPath = ResolveEntryAssemblyPath(configuration, environment, manifestDirectory, manifest.EntryAssembly);
        var shadowDirectory = ShadowCopyPlugin(manifest.Id, entryAssemblyPath);
        RegisterPluginAssemblyResolution(shadowDirectory);
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

    private static IEnumerable<string> GetStylesheetHrefs(QuasarLoadedUiPlugin plugin)
    {
        var staticAssetsDirectory = plugin.Context.StaticAssetsDirectory;
        foreach (var stylesheet in plugin.Context.Manifest.Stylesheets)
        {
            var value = stylesheet?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            {
                yield return uri.ToString();
                continue;
            }

            if (value.StartsWith("/", StringComparison.Ordinal))
            {
                yield return value;
                continue;
            }

            if (string.IsNullOrWhiteSpace(staticAssetsDirectory))
                continue;

            var normalized = value.Replace('\\', '/').TrimStart('/');
            if (normalized.Contains("../", StringComparison.Ordinal) ||
                string.Equals(normalized, "..", StringComparison.Ordinal))
            {
                continue;
            }

            yield return $"{StaticAssetRequestPathPrefix}/{plugin.Id}/{normalized}";
        }
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

    private static void RegisterPluginAssemblyResolution(string assemblyDirectory)
    {
        lock (AssemblyResolverSync)
        {
            foreach (var assemblyPath in Directory.EnumerateFiles(assemblyDirectory, "*.dll", SearchOption.AllDirectories))
            {
                try
                {
                    var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
                    if (string.IsNullOrWhiteSpace(assemblyName.Name))
                        continue;

                    var candidates = PluginAssemblyCandidates.GetValueOrDefault(assemblyName.Name);
                    if (candidates is null)
                    {
                        candidates = [];
                        PluginAssemblyCandidates[assemblyName.Name] = candidates;
                    }

                    if (!candidates.Any(candidate => string.Equals(candidate.Path, assemblyPath, StringComparison.OrdinalIgnoreCase)))
                        candidates.Add(new PluginAssemblyCandidate(assemblyPath, assemblyName));
                }
                catch
                {
                    // Non-.NET DLLs can sit beside plugin assets; ignore them for assembly probing.
                }
            }

            if (_assemblyResolverRegistered)
                return;

            AssemblyLoadContext.Default.Resolving += ResolvePluginAssembly;
            _assemblyResolverRegistered = true;
        }
    }

    private static Assembly? ResolvePluginAssembly(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName.Name))
            return null;

        PluginAssemblyCandidate? candidate;
        lock (AssemblyResolverSync)
        {
            if (!PluginAssemblyCandidates.TryGetValue(assemblyName.Name, out var candidates) || candidates.Count == 0)
                return null;

            candidate = candidates.FirstOrDefault(item => item.Name.Version == assemblyName.Version) ?? candidates[0];
        }

        return File.Exists(candidate.Path)
            ? context.LoadFromAssemblyPath(candidate.Path)
            : null;
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

    private sealed record PluginAssemblyCandidate(string Path, AssemblyName Name);
}
