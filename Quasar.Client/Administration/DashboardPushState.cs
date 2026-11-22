using Quasar.Services.Updates;
using Microsoft.JSInterop;

namespace Quasar.Client.Administration;

/// <summary>Browser subscription status shared by the WASM notifications page and bell.</summary>
public sealed class DashboardPushState(DashboardSdk sdk, IJSRuntime js)
{
    public bool Checked { get; private set; }
    public bool Supported { get; private set; }
    public bool Enabled { get; private set; }
    public bool ServiceAvailable { get; private set; } = true;
    public bool Busy { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;
    private sealed record BrowserStatus(bool Supported, BrowserPushSubscription? Subscription);

    public async Task RefreshAsync()
    {
        if (Busy) return;
        Busy = true;
        Error = null;
        Changed?.Invoke();
        try { await RefreshCoreAsync(); }
        finally { Busy = false; Changed?.Invoke(); }
    }

    private async Task RefreshCoreAsync()
    {
        Supported = false;
        ServiceAvailable = true;
        try
        {
            var status = await js.InvokeAsync<BrowserStatus>("quasarPush.status");
            Supported = status.Supported;
            if (Supported) await sdk.RequestAsync<string>("notifications.push.key");
            Enabled = status.Subscription is not null && await sdk.RequestAsync<bool>("notifications.push.status", new { endpointSecret = status.Subscription.Endpoint });
        }
        catch (Exception)
        {
            Enabled = false;
            ServiceAvailable = false;
            Error ??= "Push settings are unavailable. Try refreshing the status or check Quasar logs.";
        }
        Checked = true;
    }

    public async Task EnableAsync()
    {
        if (Busy || !Checked || !Supported || !ServiceAvailable || Enabled) return;
        Busy = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            string key = await sdk.RequestAsync<string>("notifications.push.key");
            var subscription = await js.InvokeAsync<BrowserPushSubscription>("quasarPush.subscribe", key);
            await sdk.RequestAsync<bool>("notifications.push.subscribe", new { subscriptionSecret = subscription });
            Enabled = true;
        }
        catch (JSException error) when (error.Message.Contains("Quasar push service worker", StringComparison.OrdinalIgnoreCase))
        {
            Error = "Notification service worker is not ready. Refresh this page and try again.";
        }
        catch (JSException error) when (error.Message.Contains("Registration failed - push service error", StringComparison.OrdinalIgnoreCase))
        {
            Error = "Browser push service unavailable. Check your network; in Brave, enable ‘Use Google services for push messaging’ in Settings → Privacy and security, then try again.";
        }
        catch (Exception)
        {
            Error = "Could not enable push notifications. Check browser permission and try again.";
        }
        finally { Busy = false; Changed?.Invoke(); }
    }

    public async Task DisableAsync()
    {
        if (Busy || !Enabled) return;
        Busy = true;
        Error = null;
        Changed?.Invoke();
        try
        {
            string? endpoint = await js.InvokeAsync<string?>("quasarPush.unsubscribe");
            Enabled = false;
            if (endpoint is not null)
            {
                await sdk.RequestAsync<bool>("notifications.push.unsubscribe", new { endpointSecret = endpoint });
            }
        }
        catch (Exception)
        {
            Error = "Could not finish disabling push notifications. Refresh the status and try again.";
        }
        finally { Busy = false; Changed?.Invoke(); }
    }
}
