using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Quasar.Plugin.Abstractions.Operations;

namespace Quasar.Tests;

/// <summary>Send bUnit's browser SDK calls through the fixture's registered operation handlers.</summary>
internal sealed class DashboardTestTransport(IServiceProvider services) : IJSRuntime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
    public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
    {
        if (identifier != "quasarSdk.request" || args is not [string plugin, string operation, var input])
            throw new NotSupportedException(identifier);
        var user = (await services.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync()).User;
        var result = await services.GetRequiredService<IQuasarOperationDispatcher>().DispatchAsync(new(user, services),
            plugin, operation, JsonSerializer.SerializeToElement(input, Json), token);
        if (result.Error is not null) throw new JSException(result.Error);
        return JsonSerializer.SerializeToElement(result.Value, Json).Deserialize<T>(Json)!;
    }
}
