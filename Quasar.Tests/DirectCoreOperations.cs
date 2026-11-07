using System.Security.Claims;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Plugins;

namespace Quasar.Tests;

/// <summary>For page tests: the core operations behind a dispatcher that skips authorization and auditing,
/// so a page's action runs the real operation against the fixture's services.</summary>
internal static class DirectCoreOperations
{
    public static IServiceCollection AddDirectCoreOperations(this IServiceCollection services)
    {
        services.AddQuasarCoreOperations();
        services.AddSingleton<IQuasarOperationDispatcher, Dispatcher>();
        services.AddScoped<QuasarCoreOperationCaller>();
        services.TryAddScoped<AuthenticationStateProvider, Anonymous>();
        return services;
    }

    private sealed class Dispatcher(IEnumerable<QuasarOperation> operations)
        : IQuasarOperationDispatcher
    {
        public async Task<QuasarOperationResult> DispatchAsync(
            QuasarOperationContext context,
            string pluginId,
            string operationId,
            JsonElement request,
            CancellationToken cancellationToken
        )
        {
            var operation = operations.Single(candidate =>
                candidate.PluginId == pluginId && candidate.OperationId == operationId
            );
            var undo = new QuasarUndoRecorder(
                Path.Combine(
                    MagnetarPaths.GetQuasarDirectory(),
                    "audit",
                    Guid.NewGuid().ToString("N")
                )
            );
            try
            {
                return new(
                    200,
                    await operation.InvokeAsync(
                        context with
                        {
                            Undo = undo,
                        },
                        request,
                        cancellationToken
                    )
                );
            }
            catch (QuasarOperationException error)
            {
                return new(error.StatusCode, Error: error.Message);
            }
        }
    }

    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}

/// <summary>Points the Quasar data folder at a temporary folder for the life of a test. Only for tests in a
/// collection that runs alone, since the folder is process-wide.</summary>
internal sealed class TemporaryQuasarDirectory : IDisposable
{
    private static readonly System.Reflection.FieldInfo Cache = typeof(MagnetarPaths).GetField(
        "_cachedQuasarDirectory",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
    )!;
    private readonly object? _previous = Cache.GetValue(null);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "quasar-test-" + Guid.NewGuid().ToString("N")
    );

    public TemporaryQuasarDirectory()
    {
        Directory.CreateDirectory(_root);
        Cache.SetValue(null, _root);
    }

    public void Dispose()
    {
        Cache.SetValue(null, _previous);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
    }
}
