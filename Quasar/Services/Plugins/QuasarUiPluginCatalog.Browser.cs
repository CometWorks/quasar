using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Plugins;
using Quasar.Plugin.Abstractions.Extensions;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

public sealed partial class QuasarUiPluginCatalog
{
    private sealed record BrowserArtifact(string Name, string Digest, byte[] Contents);
    private readonly Dictionary<string, BrowserArtifact> _browserArtifacts = new(StringComparer.OrdinalIgnoreCase);

    private void CollectBrowserArtifacts()
    {
        foreach (var plugin in _plugins)
        {
            if (plugin.Context.Manifest.BrowserAssembly is not { } fileName)
                continue;
            var path = Path.Combine(plugin.ShadowCopyDirectory, fileName);
            var assembly = RazorAssemblies.FirstOrDefault(candidate =>
                string.Equals(candidate.Location, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (assembly is null || !plugin.Context.Manifest.Id.Equals(plugin.Id, StringComparison.OrdinalIgnoreCase))
            {
                _loadErrors.Add($"{plugin.Id}: declared browser assembly {fileName} was not loaded from this package.");
                continue;
            }
            try
            {
                var bytes = File.ReadAllBytes(path);
                if (!_browserArtifacts.TryAdd(plugin.Id, new(assembly.FullName!, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes)))
                    _loadErrors.Add($"{plugin.Id}: duplicate browser assembly declaration.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _loadErrors.Add($"{plugin.Id}: failed to read browser assembly {fileName}: {error.Message}");
            }
        }
    }

    public IReadOnlyList<Assembly> RoutableAssemblies { get; private set; } = [];
    public BrowserPluginAssembly[] BrowserPageAssemblies { get; private set; } = [];
    private readonly Dictionary<(string Assembly, string Component), Type> _browserPages = [];

    private void CollectPluginRoutes()
    {
        if (RazorAssemblies.Count == 0) return;
        var core = typeof(Quasar.Client.Components.DashboardRouter).Assembly;
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accepted = new List<Assembly>();
        var browser = new List<BrowserPluginAssembly>();
        foreach (var assembly in new[] { typeof(Program).Assembly, core }.Concat(RazorAssemblies.OrderBy(a => a.FullName, StringComparer.Ordinal)).Distinct())
        {
            var isCore = assembly == typeof(Program).Assembly || assembly == core;
            try
            {
                var pages = assembly.GetExportedTypes().Where(t => typeof(IComponent).IsAssignableFrom(t))
                    .Select(t => (Type: t, Routes: t.GetCustomAttributes<RouteAttribute>(false).ToArray())).Where(p => p.Routes.Length != 0).ToArray();
                var keys = pages.SelectMany(p => p.Routes.Select(r => RouteKey(r.Template))).ToList();
                if (!isCore && (keys.Any(occupied.Contains) || keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Count))
                {
                    _loadErrors.Add($"{assembly.GetName().Name}: a plugin route conflicts with an existing route. The assembly's pages are disabled.");
                    continue;
                }
                occupied.UnionWith(keys);
                if (isCore || pages.Length == 0) continue;
                accepted.Add(assembly);
                var artifact = _browserArtifacts.FirstOrDefault(pair => pair.Value.Name == assembly.FullName);
                if (artifact.Value is null) continue;
                browser.Add(new(artifact.Key, artifact.Value.Name, artifact.Value.Digest));
                foreach (var page in pages) _browserPages[(artifact.Value.Name, page.Type.FullName!)] = page.Type;
            }
            catch (Exception error) when (!isCore && error is ReflectionTypeLoadException or TypeLoadException or RoutePatternException)
            {
                _loadErrors.Add($"{assembly.GetName().Name}: could not register plugin routes: {error.Message}");
            }
        }
        RoutableAssemblies = accepted;
        BrowserPageAssemblies = browser.ToArray();
    }

    // Parameter names do not distinguish endpoints, but their constraints and route segments do.
    private static string RouteKey(string template) => string.Join("/", RoutePatternFactory.Parse(template.Trim('/')).PathSegments.Select(
        segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart parameter => "{" + (parameter.IsCatchAll ? "*" : "") + ":" +
                string.Join(":", parameter.ParameterPolicies.Select(policy => policy.Content)) + (parameter.IsOptional ? "?" : "") + "}",
            _ => throw new InvalidOperationException("Unknown route segment.")
        }))));

    public bool IsBrowserPage(Type? type) => type is not null && (type.Assembly == typeof(Quasar.Client.Components.DashboardRouter).Assembly ||
        _browserPages.ContainsKey((type.Assembly.FullName!, type.FullName!)));

    internal async Task<bool> CanOpenBrowserPageAsync(QuasarOperationContext caller, string assemblyName, string component)
    {
        if (!_browserPages.TryGetValue((assemblyName, component), out var type)) return false;
        var attributes = type.GetCustomAttributes<AuthorizeAttribute>(true).Cast<IAuthorizeData>().ToArray();
        if (attributes.Length == 0) return true;
        var provider = caller.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await AuthorizationPolicy.CombineAsync(provider, attributes);
        return policy is not null && (await caller.Services.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(caller.User, null, policy)).Succeeded;
    }

    public bool RequiresServerExtensions(params string[] targets) => Extensions.Any(ext =>
        targets.Contains(ext.TargetKey, StringComparer.OrdinalIgnoreCase) &&
        (!_browserArtifacts.TryGetValue(ext.PluginId, out var artifact) || artifact.Name != ext.ComponentType.Assembly.FullName));

    internal async Task<IReadOnlyList<BrowserExtension>> AuthorizedExtensionsAsync(QuasarOperationContext caller)
    {
        var auth = caller.Services.GetRequiredService<IAuthorizationService>();
        var result = new List<BrowserExtension>();
        foreach (var ext in Extensions)
        {
            if (!_browserArtifacts.TryGetValue(ext.PluginId, out var artifact)
                || ext.ComponentType.Assembly.FullName != artifact.Name)
                continue;
            var policy = string.IsNullOrWhiteSpace(ext.Policy) && ext.TargetKey == QuasarExtensionTargets.EntityActions
                ? QuasarPolicyNames.CanControlServers : ext.Policy;
            if (!string.IsNullOrWhiteSpace(policy) && !(await auth.AuthorizeAsync(caller.User, null, policy)).Succeeded)
                continue;
            result.Add(new(ext.TargetKey, ext.PluginId, ext.Mode.ToString(), ext.Priority, ext.ComponentType.FullName!, artifact.Name, artifact.Digest));
        }
        return result;
    }

    private void MapBrowserArtifactEndpoint(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/_quasar/sdk/v1/ui/assemblies/{pluginId}/{digest}", (string pluginId, string digest, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            return _browserArtifacts.TryGetValue(pluginId, out var artifact) && artifact.Digest == digest
                ? Results.Bytes(artifact.Contents, "application/octet-stream") : Results.NotFound();
        }).RequireAuthorization(QuasarPolicyNames.CanView);
    }
}
