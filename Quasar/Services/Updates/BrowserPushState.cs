using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace Quasar.Services.Updates;

/// <summary>Browser-local push state shared by the notification page and bell in one Blazor circuit.</summary>
public sealed class BrowserPushState(PushNotificationService subscriptions, IJSRuntime js,
    AuthenticationStateProvider authentication, ILogger<BrowserPushState> logger)
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
            if (Supported) await subscriptions.GetPublicKeyAsync();
            var user = (await authentication.GetAuthenticationStateAsync()).User;
            Enabled = status.Subscription is not null && await subscriptions.IsRegisteredAsync(user, status.Subscription.Endpoint);
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Could not read this browser's push subscription.");
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
            var user = (await authentication.GetAuthenticationStateAsync()).User;
            string key = await subscriptions.GetPublicKeyAsync();
            var subscription = await js.InvokeAsync<BrowserPushSubscription>("quasarPush.subscribe", key);
            await subscriptions.SubscribeAsync(user, subscription);
            Enabled = true;
        }
        catch (JSException error) when (error.Message.Contains("Quasar push service worker", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(error, "Quasar's push service worker did not activate.");
            Error = "Notification service worker is not ready. Refresh this page and try again.";
        }
        catch (JSException error) when (error.Message.Contains("Registration failed - push service error", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(error, "The browser's push service could not register a subscription.");
            Error = "Browser push service unavailable. Check your network.";
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Could not enable browser push notifications.");
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
                var user = (await authentication.GetAuthenticationStateAsync()).User;
                await subscriptions.UnsubscribeAsync(user, endpoint);
            }
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Could not disable browser push notifications.");
            Error = "Could not finish disabling push notifications. Refresh the status and try again.";
        }
        finally { Busy = false; Changed?.Invoke(); }
    }
}
