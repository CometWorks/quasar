namespace Quasar.Services.Plugins;

public sealed class QuasarUiPluginHubRefreshService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(8);

    private readonly QuasarUiPluginHubCatalogService _catalog;
    private readonly ILogger<QuasarUiPluginHubRefreshService> _logger;

    public QuasarUiPluginHubRefreshService(
        QuasarUiPluginHubCatalogService catalog,
        ILogger<QuasarUiPluginHubRefreshService> logger)
    {
        _catalog = catalog;
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
            await _catalog.RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Scheduled QuasarHub refresh failed; keeping cached catalog.");
        }
    }
}
