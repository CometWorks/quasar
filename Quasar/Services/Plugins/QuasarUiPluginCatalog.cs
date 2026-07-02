using System.Reflection;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Quasar.Plugin.Abstractions.Extensions;
using Quasar.Plugin.Abstractions.Navigation;

namespace Quasar.Services.Plugins;

public sealed class QuasarUiPluginCatalog
{
    public const string StaticAssetRequestPathPrefix = "/_quasar/plugins";

    private readonly IReadOnlyList<QuasarLoadedUiPlugin> _plugins;

    private QuasarUiPluginCatalog(
        IReadOnlyList<QuasarLoadedUiPlugin> plugins,
        bool safeMode,
        IReadOnlyList<string> safeModeReasons)
    {
        _plugins = plugins;
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

    public IReadOnlyList<QuasarLoadedUiPlugin> LoadedPlugins => _plugins;

    public IReadOnlyList<Assembly> RazorAssemblies { get; }

    public IReadOnlyList<QuasarNavItem> NavItems { get; }

    public IReadOnlyList<QuasarExtensionContribution> Extensions { get; }

    public static QuasarUiPluginCatalog Create(IConfiguration configuration, IHostEnvironment environment)
    {
        var safeMode = IsSafeModeEnabled(configuration, out var safeModeReasons);
        if (safeMode)
            return new QuasarUiPluginCatalog([], safeMode: true, safeModeReasons);

        // Dynamic discovery/loading lands in the next implementation step. The
        // catalog is still registered now so routes, endpoints, and DI have one
        // stable integration point.
        return new QuasarUiPluginCatalog([], safeMode: false, safeModeReasons: []);
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
}
