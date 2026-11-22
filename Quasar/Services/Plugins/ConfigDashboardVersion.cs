namespace Quasar.Services.Plugins;

/// <summary>A cheap poll token for the catalogs that used to notify the server-side editor directly.</summary>
internal sealed class ConfigDashboardVersion : IDisposable
{
    private readonly QuasarConfigProfileCatalog _profiles;
    private readonly DedicatedServerCatalog _servers;
    private readonly ClusterCatalog? _clusters;
    private readonly QuasarPluginCatalogService? _plugins;
    private readonly QuasarDevFolderCatalog? _folders;
    private readonly QuasarPluginHubCatalog? _hubs;
    private readonly SteamWorkshopCredentialsCatalog? _credentials;
    private readonly string _instance = Guid.NewGuid().ToString("N");
    private long _revision;

    public ConfigDashboardVersion(IServiceProvider services)
    {
        _profiles = services.GetRequiredService<QuasarConfigProfileCatalog>();
        _servers = services.GetRequiredService<DedicatedServerCatalog>();
        _clusters = services.GetService<ClusterCatalog>();
        _plugins = services.GetService<QuasarPluginCatalogService>();
        _folders = services.GetService<QuasarDevFolderCatalog>();
        _hubs = services.GetService<QuasarPluginHubCatalog>();
        _credentials = services.GetService<SteamWorkshopCredentialsCatalog>();
        _profiles.Changed += Changed;
        _servers.Changed += Changed;
        if (_clusters is not null) _clusters.Changed += Changed;
        if (_folders is not null) _folders.Changed += Changed;
        if (_hubs is not null) _hubs.Changed += Changed;
        if (_credentials is not null) _credentials.Changed += Changed;
    }

    internal string Token => $"{_instance}/{Interlocked.Read(ref _revision)}/{_plugins?.LastRefreshUtc?.Ticks}";
    private void Changed() => Interlocked.Increment(ref _revision);

    public void Dispose()
    {
        _profiles.Changed -= Changed;
        _servers.Changed -= Changed;
        if (_clusters is not null) _clusters.Changed -= Changed;
        if (_folders is not null) _folders.Changed -= Changed;
        if (_hubs is not null) _hubs.Changed -= Changed;
        if (_credentials is not null) _credentials.Changed -= Changed;
    }
}
