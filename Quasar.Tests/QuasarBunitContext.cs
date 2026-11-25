using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Quasar.Client.Ui;
using Quasar.Services;

namespace Quasar.Tests;

/// <summary>A bUnit context with the scoped services every Quasar component may inject.</summary>
public class QuasarBunitContext : BunitContext
{
    internal static async Task<T> ReadPolicyAsync<T>(IServiceProvider services, object?[] args)
    {
        var principal = (await services.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync()).User;
        return (T)(object)(await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(principal, (string)args[0]!)).Succeeded;
    }

    public QuasarBunitContext()
    {
        Services.AddScoped<ViewerTimeZone>();
        Services.AddQuasarUi();
        // Native dialogs complete when the browser raises the dialog close event.
        // bUnit has no browser event loop, so emulate that callback for tests.
        Services.AddScoped<IJSRuntime>(_ => new QuasarDialogJsRuntime(JSInterop.JSRuntime));
    }
}
