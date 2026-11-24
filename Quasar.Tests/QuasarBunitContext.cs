using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
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

    public QuasarBunitContext() => Services.AddScoped<ViewerTimeZone>();
}
