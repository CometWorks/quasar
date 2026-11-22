using System.Security.Cryptography;
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
            var bytes = File.ReadAllBytes(path);
            _browserArtifacts.Add(plugin.Id, new(assembly.FullName!, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes));
        }
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
