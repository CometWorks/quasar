using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.JSInterop;
using Quasar.Models;
using Quasar.Services;

namespace Quasar.Client.Configs;

public sealed record ConfigRevision(string Token, bool CanEdit);
public sealed record ConfigAssignment(string UniqueName, string ConfigProfileId);
public sealed record ConfigTemplate(string WorldTemplateId, string Name);
public sealed record ConfigDirectory(string Path, bool IsWorld, IReadOnlyList<FileBrowserShortcut> Shortcuts,
    IReadOnlyList<FileBrowserBreadcrumb> Breadcrumbs, IReadOnlyList<FileBrowserEntry> Directories, IReadOnlyList<FileBrowserFileEntry> XmlFiles);
public sealed class ConfigSnapshot
{
    public string Version { get; init; } = "";
    public bool CanEdit { get; init; }
    public IReadOnlyList<QuasarConfigProfile> Profiles { get; init; } = [];
    public IReadOnlyList<ConfigAssignment> Servers { get; init; } = [];
    public IReadOnlyList<QuasarDevFolderSelection> DevFolders { get; init; } = [];
    public IReadOnlyList<QuasarPluginCatalogEntry> Entries { get; init; } = [];
    public IReadOnlyList<QuasarPluginCatalogEntry> HubEntries { get; init; } = [];
    public IReadOnlyList<QuasarPluginHubStatus> HubStatuses { get; init; } = [];
    public string LastError { get; init; } = "";
    public bool HasWebApiKey { get; init; }
    public string SteamUsername { get; init; } = "";
}

internal sealed class ConfigDashboard(IJSRuntime js)
{
    // Preserve Steam IDs and Workshop IDs across the JavaScript transport in both directions.
    internal static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString
    };
    internal async Task<T> ReadAsync<T>(string operation, object? input = null)
    {
        var result = await js.InvokeAsync<JsonElement>("quasarSdk.readJson", "quasar.core", "configs." + operation,
            JsonSerializer.SerializeToElement(input ?? new {}, Wire));
        return result.Deserialize<T>(Wire)!;
    }
    internal async Task<string> StoreProfileAsync(QuasarConfigProfile profile, bool create)
    {
        var input = new { profile };
        string json = JsonSerializer.Serialize(input);
        if (System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(input, Wire)) > 64 * 1024)
            return await js.InvokeAsync<string>("quasarSdk.uploadJson", "quasar.core", "configs.profile.upload", new { create }, json);
        if (create) return await WriteAsync<string>("configs.create", input);
        await WriteAsync<JsonElement>("config.profile.save", input);
        return profile.ConfigProfileId;
    }
    internal async Task<T> WriteAsync<T>(string operation, object input)
    {
        var result = await js.InvokeAsync<JsonElement>("quasarSdk.request", "quasar.core", operation,
            JsonSerializer.SerializeToElement(input, Wire));
        return result.Deserialize<T>(Wire)!;
    }
}
