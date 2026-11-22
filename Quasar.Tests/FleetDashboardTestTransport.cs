using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Quasar.Plugin.Abstractions.Operations;
using Xunit;

namespace Quasar.Tests;

internal sealed class FleetDashboardTestTransport(IServiceProvider services) : IJSRuntime
{
    public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
        InvokeAsync<T>(identifier, default, args);

    public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
    {
        Assert.Equal("quasarSdk.request", identifier);
        Assert.Equal("quasar.core", args![0]);
        var operation = services.GetServices<QuasarOperation>().Single(o => o.OperationId == (string)args[1]!);
        var principal = (await services.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync()).User;
        try
        {
            var authorization = services.GetRequiredService<IAuthorizationService>();
            foreach (var policy in operation.DefaultPolicies)
                if (!(await authorization.AuthorizeAsync(principal, policy)).Succeeded)
                    throw new QuasarOperationException(403, "You do not have permission to perform this operation.");
            var value = await operation.InvokeAsync(new(principal, services), JsonSerializer.SerializeToElement(args[2]), token);
            return JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value))!;
        }
        catch (QuasarOperationException error) { throw new JSException(error.Message); }
    }
}
