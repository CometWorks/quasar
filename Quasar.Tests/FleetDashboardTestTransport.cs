using System.Text.Json;
using Quasar.Client.Fleet;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Quasar.Plugin.Abstractions.Operations;
using Xunit;

namespace Quasar.Tests;

internal sealed class FleetDashboardTestTransport(IServiceProvider services) : IJSRuntime
{
    internal static FleetRow Row(string id = "alpha", string display = "Alpha", bool cluster = false, int port = 27016) => new(id, display, cluster, "Stopped", "Off", port,
            "", "", "", null, null, false, null, null, null, null, null, null, "Unknown", "", null, null, [], false, false, false, default);

    public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) =>
        InvokeAsync<T>(identifier, default, args);

    public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
    {
        if (identifier == "quasarSdk.allowed") return await QuasarBunitContext.ReadPolicyAsync<T>(services, args!);
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
