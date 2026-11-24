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
    internal byte[]? SelectedZip { get; set; }

    internal OperationsBrowserContext(IJSRuntime? storage = null, Func<string, Stream>? openFile = null)
    {
        Services.AddQuasarCoreOperations();
        Services.AddScoped<DashboardSdk>();
        Services.AddScoped<IJSRuntime>(provider => new BrowserInterop(JSInterop.JSRuntime, provider, storage, () => SelectedZip, openFile));
    }

    private sealed class BrowserInterop(IJSRuntime javascript, IServiceProvider services, IJSRuntime? storage, Func<byte[]?> selectedZip, Func<string, Stream>? openFile) : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => InvokeAsync<T>(name, default, args);

        public async ValueTask<T> InvokeAsync<T>(string name, CancellationToken token, object?[]? args)
        {
            if (name == "quasarSdk.allowed") return await QuasarBunitContext.ReadPolicyAsync<T>(services, args!);
            if (name == "import" && args?[0] is string module && module.EndsWith("/worldTransfers.js", StringComparison.Ordinal))
                return (T)(object)new TransferModule(this, selectedZip);
            if (storage is not null && name is ("quasarConfigs.getLocalStorage" or "quasarConfigs.setLocalStorage"))
                return await storage.InvokeAsync<T>(name, token, args);
            if (name is not ("quasarSdk.readJson" or "quasarSdk.readText" or "quasarSdk.request" or "quasarSdk.uploadJson" or "quasarSdk.uploadFile")) return await javascript.InvokeAsync<T>(name, token, args);
            return await InvokeOperation<T>(name, args!, token);
        }

        private sealed class TransferModule(BrowserInterop interop, Func<byte[]?> selectedZip) : IJSObjectReference
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
            public ValueTask<T> InvokeAsync<T>(string name, object?[]? args) => InvokeAsync<T>(name, default, args);
            public async ValueTask<T> InvokeAsync<T>(string name, CancellationToken token, object?[]? args)
            {
                if (name == "cancel") return default!;
                if (name != "write") throw new NotSupportedException(name);
                var bytes = args![4] is null ? JsonSerializer.SerializeToUtf8Bytes(args[3], JsonSerializerOptions.Web)
                    : selectedZip() ?? throw new InvalidOperationException("No ZIP selected by test.");
                if (Equals(args[1], "server-import.zip"))
                {
                    var target = args[2]!;
                    var start = new { target, size = bytes.Length, directoryOffset = bytes.Length };
                    var range = await interop.InvokeOperation<ZipSlice>(name, ["quasar.core", "server-import.zip.start", start], token);
                    while (range.Sequence != 0)
                    {
                        using var selected = new MemoryStream(bytes, (int)range.Offset, (int)range.Length, writable: false);
                        var request = new { target, range.Sequence };
                        range = await interop.InvokeOperation<ZipSlice>(name, ["quasar.core", "server-import.zip.range", request], token, selected);
                    }
                    return (T)(object)true;
                }
                using var stream = new MemoryStream(bytes, writable: false);
                return await interop.InvokeOperation<T>(name, ["quasar.core", args[1], args[2]], token, stream);
            }
        }

        private async ValueTask<T> InvokeOperation<T>(string name, object?[] args, CancellationToken token, Stream? transfer = null)
        {
            var id = (string)args[1]!;
            var operation = services.GetServices<QuasarOperation>().Single(o => o.OperationId == id && o.PluginId == (string)args[0]!);
            var user = (await services.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync()).User;
            var authorization = services.GetRequiredService<IAuthorizationService>();
            foreach (var policy in operation.DefaultPolicies)
                if (!(await authorization.AuthorizeAsync(user, null, policy)).Succeeded) throw new JSException($"Permission denied: {policy}.");
            var undoFolder = Path.Combine(Path.GetTempPath(), "quasar-test-undo-" + Guid.NewGuid().ToString("N"));
            await using var upload = name switch
            {
                "quasarSdk.uploadJson" => new MemoryStream(System.Text.Encoding.UTF8.GetBytes((string)args[3]!)),
                "quasarSdk.uploadFile" => openFile?.Invoke((string)args[3]!) ?? throw new InvalidOperationException("No selected test file."),
                _ => null
            };
            if (name == "quasarSdk.uploadFile" && upload!.Length != Convert.ToInt64(args[4])) throw new JSException("File length changed.");
            var caller = new QuasarOperationContext(user, services) { Kind = operation.Kind, Upload = upload ?? transfer, Undo = new(undoFolder), VerifyAccess = _ => Task.CompletedTask };
            try
            {
                var input = JsonSerializer.SerializeToElement(args[name == "quasarSdk.uploadFile" ? 5 : 2], JsonSerializerOptions.Web);
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
