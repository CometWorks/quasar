using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Quasar.Plugin.Abstractions.Operations;

namespace Quasar.Services.Plugins;

/// <summary>Lets a Razor page run a core operation through the dispatcher as the signed-in person, so the
/// change is authorized and audited like one from the SDK or the assistant.</summary>
public sealed class QuasarCoreOperationCaller(
    IQuasarOperationDispatcher dispatcher,
    AuthenticationStateProvider authentication,
    IServiceProvider services
)
{
    /// <summary>Throws <see cref="QuasarOperationException"/> with the refusal or failure message.</summary>
    public async Task<TResponse> CallAsync<TResponse>(
        string operationId,
        object request,
        CancellationToken token = default
    )
    {
        var user = (await authentication.GetAuthenticationStateAsync()).User;
        var result = await dispatcher.DispatchAsync(
            new(user, services),
            QuasarCoreOperations.PluginId,
            operationId,
            JsonSerializer.SerializeToElement(request, QuasarCoreOperations.Json),
            token
        );
        if (result.Error is not null)
            throw new QuasarOperationException(result.StatusCode, result.Error);
        return (TResponse)result.Value!;
    }
}

public static class QuasarCoreOperationCallerExtensions
{
    // Pages resolve the caller when they act, so components rendered without the dispatcher (tests, previews) still render.
    public static Task<object?> CallCoreOperationAsync(
        this IServiceProvider services,
        string operationId,
        object request
    ) =>
        services
            .GetRequiredService<QuasarCoreOperationCaller>()
            .CallAsync<object?>(operationId, request);
}
