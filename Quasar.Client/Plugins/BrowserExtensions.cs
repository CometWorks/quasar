using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Components;

namespace Quasar.Client.Plugins;

public sealed record LoadedExtension(BrowserExtension Descriptor, Type ComponentType);

public static class BrowserExtensions
{
    public static async Task<List<LoadedExtension>> LoadAsync(DashboardSdk sdk, NavigationManager navigation,
        IEnumerable<string> targets, CancellationToken token)
    {
        var requested = targets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalog = await sdk.RequestAsync<BrowserExtension[]>("dashboard.extensions", token: token);
        var result = new List<LoadedExtension>();
        foreach (var group in catalog.Where(entry => requested.Contains(entry.TargetKey)).GroupBy(entry => entry.Digest))
        {
            var descriptor = group.First();
            var assembly = await LoadAssemblyAsync(new(descriptor.PluginId, descriptor.AssemblyName, descriptor.Digest), navigation.BaseUri, token);
            foreach (var entry in group)
            {
                var type = assembly.GetType(entry.Component, throwOnError: true)!;
                if (!typeof(IComponent).IsAssignableFrom(type))
                    throw new InvalidDataException($"{entry.Component} is not a Razor component.");
                result.Add(new(entry, type));
            }
        }
        return result;
    }

    // The drawer, router and page outlets share one verified download per panel asset.
    private static readonly ConcurrentDictionary<Uri, Lazy<Task<Assembly>>> Assemblies = new();
    private static readonly HttpClient Http = new();

    internal static async Task<Assembly> LoadAssemblyAsync(BrowserPluginAssembly descriptor, string baseUri, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = $"_quasar/sdk/v1/ui/assemblies/{Uri.EscapeDataString(descriptor.PluginId)}/{descriptor.Digest}";
        var uri = new Uri(new Uri(baseUri), path);
        var entry = Assemblies.GetOrAdd(uri, _ => new Lazy<Task<Assembly>>(() => DownloadAsync(uri, descriptor)));
        try { return await entry.Value.WaitAsync(token); }
        catch when (entry.Value.IsFaulted || entry.Value.IsCanceled)
        {
            Assemblies.TryRemove(new KeyValuePair<Uri, Lazy<Task<Assembly>>>(uri, entry));
            throw;
        }
    }

    private static async Task<Assembly> DownloadAsync(Uri uri, BrowserPluginAssembly descriptor)
    {
        var bytes = await Http.GetByteArrayAsync(uri);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), descriptor.Digest, StringComparison.Ordinal))
            throw new InvalidDataException($"The downloaded {descriptor.PluginId} assembly failed verification.");
        var assembly = Assembly.Load(bytes);
        if (assembly.FullName != descriptor.AssemblyName)
            throw new InvalidDataException($"Unexpected assembly identity from {descriptor.PluginId}.");
        return assembly;
    }
}
