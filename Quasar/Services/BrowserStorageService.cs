using System.Text.Json;
using Microsoft.JSInterop;

namespace Quasar.Services;

public sealed class BrowserStorageService(IJSRuntime js)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask<string?> GetStringAsync(string key)
    {
        var value = await GetRawAsync(key);
        if (string.IsNullOrEmpty(value))
            return value;

        if (value[0] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(value, JsonOptions);
            }
            catch (JsonException)
            {
            }
        }

        return value;
    }

    public async ValueTask<T?> GetJsonAsync<T>(string key)
    {
        var value = await GetRawAsync(key);
        return string.IsNullOrWhiteSpace(value)
            ? default
            : JsonSerializer.Deserialize<T>(value, JsonOptions);
    }

    public ValueTask SetStringAsync(string key, string value) =>
        js.InvokeVoidAsync("quasarConfigs.setLocalStorage", key, value);

    public ValueTask SetJsonAsync<T>(string key, T value) =>
        SetStringAsync(key, JsonSerializer.Serialize(value, JsonOptions));

    private ValueTask<string?> GetRawAsync(string key) =>
        js.InvokeAsync<string?>("quasarConfigs.getLocalStorage", key);
}
