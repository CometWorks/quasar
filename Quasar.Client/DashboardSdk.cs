using Microsoft.JSInterop;

namespace Quasar.Client;

/// <summary>First-party pages use the same authenticated browser transport as UI plugins.</summary>
public sealed class DashboardSdk(IJSRuntime js)
{
    public async Task<T> RequestAsync<T>(string operation, object? payload = null, CancellationToken token = default) =>
        await js.InvokeAsync<T>("quasarSdk.request", token, "quasar.core", operation, payload ?? new { });

    public async Task<string> ReadTextAsync(string operation, object payload, CancellationToken token = default) =>
        await js.InvokeAsync<string>("quasarSdk.readText", token, "quasar.core", operation, payload);

    public async Task DownloadAsync(string operation, object? payload = null, CancellationToken token = default) =>
        await js.InvokeVoidAsync("quasarSdk.download", token, "quasar.core", operation, payload ?? new { });

    internal ValueTask<DashboardAccess> GetAccessAsync() =>
        js.InvokeAsync<DashboardAccess>("quasarSdk.dashboardAccess");

    public async Task<bool> AllowedAsync(string policy, CancellationToken token = default) =>
        await js.InvokeAsync<bool>("quasarSdk.allowed", token, policy);
}
