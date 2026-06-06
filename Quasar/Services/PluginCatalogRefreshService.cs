namespace Quasar.Services;

public sealed class PluginCatalogRefreshService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(8);

    private readonly QuasarPluginCatalogService _pluginCatalog;
    private readonly ILogger<PluginCatalogRefreshService> _logger;

    public PluginCatalogRefreshService(
        QuasarPluginCatalogService pluginCatalog,
        ILogger<PluginCatalogRefreshService> logger)
    {
        _pluginCatalog = pluginCatalog;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            await RefreshOnceAsync(stoppingToken);

            using var timer = new PeriodicTimer(RefreshInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RefreshOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _pluginCatalog.RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // RefreshAsync already records LastError and logs; keep the cached catalog as fallback.
            _logger.LogWarning(exception, "Scheduled plugin catalog refresh failed; keeping cached catalog.");
        }
    }
}
