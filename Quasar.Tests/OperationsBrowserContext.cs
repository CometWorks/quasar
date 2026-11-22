using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Quasar.Client;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Plugins;

namespace Quasar.Tests;

/// <summary>Render the production browser components in bUnit, routing their SDK interop to the actual worker handlers.</summary>
internal sealed class OperationsBrowserContext : QuasarBunitContext
{
    internal OperationsBrowserContext(IJSRuntime? storage = null)
    {
        Services.AddQuasarCoreOperations();
        Services.AddScoped<DashboardSdk>();
        Services.AddScoped<IJSRuntime>(provider => new BrowserInterop(JSInterop.JSRuntime, provider, storage));
    }

    private sealed class BrowserInterop(IJSRuntime javascript, IServiceProvider services, IJSRuntime? storage) : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => InvokeAsync<T>(name, default, args);

        public async ValueTask<T> InvokeAsync<T>(string name, CancellationToken token, object?[]? args)
        {
            if (storage is not null && name is ("quasarConfigs.getLocalStorage" or "quasarConfigs.setLocalStorage"))
                return await storage.InvokeAsync<T>(name, token, args);
            if (name is not ("quasarSdk.readJson" or "quasarSdk.readText" or "quasarSdk.request")) return await javascript.InvokeAsync<T>(name, token, args);
            var id = (string)args![1]!;
            var operation = services.GetServices<QuasarOperation>().Single(o => o.OperationId == id && o.PluginId == (string)args[0]!);
            var user = (await services.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync()).User;
            var authorization = services.GetRequiredService<IAuthorizationService>();
            foreach (var policy in operation.DefaultPolicies)
                if (!(await authorization.AuthorizeAsync(user, null, policy)).Succeeded) throw new JSException($"Permission denied: {policy}.");
            var undoFolder = Path.Combine(Path.GetTempPath(), "quasar-test-undo-" + Guid.NewGuid().ToString("N"));
            var caller = new QuasarOperationContext(user, services) { Kind = operation.Kind, Undo = new(undoFolder), VerifyAccess = _ => Task.CompletedTask };
            try
            {
                var input = JsonSerializer.SerializeToElement(args[2], JsonSerializerOptions.Web);
                var value = await operation.InvokeAsync(caller, input, token);
                if (value is QuasarRead read)
                {
                    await using var stream = await read.OpenAsync(token);
                    if (name == "quasarSdk.readText")
                    {
                        using var reader = new StreamReader(stream);
                        return (T)(object)await reader.ReadToEndAsync(token);
                    }
                    return (await JsonSerializer.DeserializeAsync<T>(stream, JsonSerializerOptions.Web, token))!;
                }
                return JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web).Deserialize<T>(JsonSerializerOptions.Web)!;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { throw new JSException(ex.Message); }
            finally { if (Directory.Exists(undoFolder)) Directory.Delete(undoFolder, recursive: true); }
        }
    }
}
