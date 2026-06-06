using Quasar.Models;

namespace Quasar.Services.Backup;

/// <summary>
/// Background scheduler that writes automatic configuration backups into the
/// Backups directory according to <see cref="QuasarBackupSettings"/> and prunes
/// old ones to the configured retention count. Modeled on the
/// <see cref="PluginCatalogRefreshService"/> PeriodicTimer pattern.
/// </summary>
public sealed class AutomaticBackupService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly QuasarBackupService _backupService;
    private readonly QuasarBackupSettingsService _settingsService;
    private readonly ILogger<AutomaticBackupService> _logger;

    public AutomaticBackupService(
        QuasarBackupService backupService,
        QuasarBackupSettingsService settingsService,
        ILogger<AutomaticBackupService> logger)
    {
        _backupService = backupService;
        _settingsService = settingsService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            using var timer = new PeriodicTimer(TickInterval);
            do
            {
                await RunDueBackupAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Performs a scheduled backup immediately, regardless of the configured
    /// schedule or whether automatic backups are enabled. Used by the "Make a
    /// backup now" action on the Backup page.
    /// </summary>
    public Task RunBackupNowAsync(CancellationToken cancellationToken = default) =>
        PerformBackupAsync(DateTimeOffset.Now, cancellationToken);

    private async Task RunDueBackupAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = _settingsService.GetSettings();
            if (!settings.Enabled)
                return;

            var now = DateTimeOffset.Now;
            if (!IsDue(settings, now))
                return;

            await PerformBackupAsync(now, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Scheduled configuration backup failed.");
        }
    }

    private async Task PerformBackupAsync(DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        var settings = _settingsService.GetSettings();
        await _backupService.WriteBackupFileAsync(timestamp, automatic: true, cancellationToken);
        _backupService.PruneAutomaticBackups(settings.RetentionCount);
        await _settingsService.UpdateLastBackupAsync(timestamp, cancellationToken);
    }

    private static bool IsDue(QuasarBackupSettings settings, DateTimeOffset now)
    {
        // First run after enabling happens at the next tick.
        if (settings.LastBackupUtc is not { } last)
            return true;

        var nextDue = ComputeNextDueLocal(last.ToLocalTime().DateTime, settings);
        return now.ToLocalTime().DateTime >= nextDue;
    }

    private static DateTime ComputeNextDueLocal(DateTime lastLocal, QuasarBackupSettings settings)
    {
        switch (settings.Frequency)
        {
            case BackupFrequency.Hourly:
                return lastLocal.AddHours(1);

            case BackupFrequency.Weekly:
            {
                var candidate = lastLocal.Date + settings.TimeOfDay.ToTimeSpan();
                while (candidate <= lastLocal || candidate.DayOfWeek != settings.DayOfWeek)
                    candidate = candidate.AddDays(1);
                return candidate;
            }

            case BackupFrequency.Daily:
            default:
            {
                var candidate = lastLocal.Date + settings.TimeOfDay.ToTimeSpan();
                while (candidate <= lastLocal)
                    candidate = candidate.AddDays(1);
                return candidate;
            }
        }
    }
}
