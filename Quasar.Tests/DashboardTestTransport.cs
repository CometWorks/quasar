using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Quasar.Plugin.Abstractions.Operations;

namespace Quasar.Tests;

/// <summary>Send bUnit's browser SDK calls through the fixture's registered operation handlers.</summary>
internal sealed class DashboardTestTransport(IServiceProvider services, Func<string, object?>? projection = null) : IJSRuntime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
    public async ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
    {
        if (identifier != "quasarSdk.request" || args is not [string plugin, string operation, var input])
            throw new NotSupportedException(identifier);
        if (projection?.Invoke(operation) is { } value) return JsonSerializer.SerializeToElement(value, Json).Deserialize<T>(Json)!;
        var user = (await services.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync()).User;
        async Task Verify(CancellationToken check)
        {
            check.ThrowIfCancellationRequested();
            var registration = services.GetServices<QuasarOperation>().Single(op => op.PluginId == plugin && op.OperationId == operation);
            foreach (var policy in registration.DefaultPolicies)
                if (!(await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, policy)).Succeeded)
                    throw new QuasarOperationException(403, "Access denied: administrator permission is required.");
        }
        var result = await services.GetRequiredService<IQuasarOperationDispatcher>().DispatchAsync(new(user, services) { VerifyAccess = Verify },
            plugin, operation, JsonSerializer.SerializeToElement(input, Json), token);
        if (result.Error is not null) throw new JSException(result.Error);
        return JsonSerializer.SerializeToElement(result.Value, Json).Deserialize<T>(Json)!;
    }
}
