using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.StaticAssets;

namespace Quasar.Services;

internal static class DashboardFrameworkAssets
{
    internal static void MapDashboardStaticAssets(this WebApplication app, bool requireAuthorization, string? manifest = null)
    {
        var assets = app.MapStaticAssets(manifest);
        if (!requireAuthorization) return;
        // Panel-relative validators, including 304 responses, belong to the current viewer.
        // Nebula serves individually verified public release files at its own independent URLs.
        assets.Add(builder =>
        {
            if (builder is RouteEndpointBuilder route &&
                route.RoutePattern.RawText?.TrimStart('/').StartsWith("_framework/", StringComparison.OrdinalIgnoreCase) == true)
            {
                builder.Metadata.Add(new AuthorizeAttribute(Auth.QuasarPolicyNames.CanView));
                var serve = builder.RequestDelegate!;
                builder.RequestDelegate = context =>
                {
                    context.Response.OnStarting(() =>
                    {
                        context.Response.Headers.CacheControl = "private, no-cache, must-revalidate";
                        return Task.CompletedTask;
                    });
                    return serve(context);
                };
            }
        });
    }
}


// StaticAssetDescriptor carries the per-file SRI even when Blazor omits binary
// resources from its UI-facing ResourceAssetCollection.
internal sealed class DashboardReleaseManifest(EndpointDataSource endpoints)
{
    private readonly Lazy<Dictionary<string, string>> _hashes = new(() => endpoints.Endpoints
        .Select(endpoint => endpoint.Metadata.GetMetadata<StaticAssetDescriptor>())
        .Where(asset => asset is not null && asset.Selectors.Count == 0
            && asset.Route.StartsWith("_framework/", StringComparison.Ordinal)
            && (asset.Route.EndsWith(".wasm", StringComparison.Ordinal) || asset.Route.EndsWith(".dat", StringComparison.Ordinal))
            && asset.Properties.Any(property => property.Name == "fingerprint"))
        .Select(asset => (Name: asset!.Route["_framework/".Length..], Hash: asset.Properties.FirstOrDefault(p => p.Name == "integrity")?.Value))
        .Where(asset => asset.Hash is not null).ToDictionary(asset => asset.Name, asset => asset.Hash!));
    public IReadOnlyDictionary<string, string> Hashes => _hashes.Value;
}
