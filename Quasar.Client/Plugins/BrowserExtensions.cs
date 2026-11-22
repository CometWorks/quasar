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
        using var http = new HttpClient { BaseAddress = new Uri(navigation.BaseUri) };
        foreach (var group in catalog.Where(entry => requested.Contains(entry.TargetKey)).GroupBy(entry => entry.Digest))
        {
            var descriptor = group.First();
            var assembly = await LoadAssemblyAsync(new(descriptor.PluginId, descriptor.AssemblyName, descriptor.Digest), http, token);
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

    private static readonly Dictionary<string, Assembly> Assemblies = new(StringComparer.Ordinal);

    internal static async Task<Assembly> LoadAssemblyAsync(BrowserPluginAssembly descriptor, HttpClient http, CancellationToken token)
    {
        if (Assemblies.TryGetValue(descriptor.Digest, out var loaded)) return loaded;
        var path = $"_quasar/sdk/v1/ui/assemblies/{Uri.EscapeDataString(descriptor.PluginId)}/{descriptor.Digest}";
        var bytes = await http.GetByteArrayAsync(path, token);
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), descriptor.Digest, StringComparison.Ordinal))
            throw new InvalidDataException($"The downloaded {descriptor.PluginId} assembly failed verification.");
        var assembly = Assembly.Load(bytes);
        if (assembly.FullName != descriptor.AssemblyName)
            throw new InvalidDataException($"Unexpected assembly identity from {descriptor.PluginId}.");
        return Assemblies[descriptor.Digest] = assembly;
    }
}
