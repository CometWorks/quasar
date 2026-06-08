using Quasar.Models;

namespace Quasar.Services.Backup;

/// <summary>
/// Background scheduler that writes automatic backups into the
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
    private readonly DedicatedServerCatalog _servers;
    private readonly ILogger<AutomaticBackupService> _logger;

    public AutomaticBackupService(
        QuasarBackupService backupService,
        QuasarBackupSettingsService settingsService,
        DedicatedServerCatalog servers,
        ILogger<AutomaticBackupService> logger)
    {
        _backupService = backupService;
        _settingsService = settingsService;
        _servers = servers;
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
    /// Performs all enabled automatic-backup rules immediately, regardless of
    /// schedule. Used by the Backup page.
    /// </summary>
    public async Task<int> RunEnabledBackupsNowAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsService.GetSettings();
        var now = DateTimeOffset.Now;
        var created = 0;

        if (settings.Configuration.Enabled)
            created += await PerformBackupAsync(QuasarBackupKind.Configuration, settings.Configuration, now, cancellationToken);
        if (settings.Server.Enabled)
            created += await PerformBackupAsync(QuasarBackupKind.Server, settings.Server, now, cancellationToken);
        if (settings.World.Enabled)
            created += await PerformBackupAsync(QuasarBackupKind.World, settings.World, now, cancellationToken);

        return created;
    }

    private async Task RunDueBackupAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = _settingsService.GetSettings();
            var now = DateTimeOffset.Now;

            await RunDueRuleAsync(QuasarBackupKind.Configuration, settings.Configuration, now, cancellationToken);
            await RunDueRuleAsync(QuasarBackupKind.Server, settings.Server, now, cancellationToken);
            await RunDueRuleAsync(QuasarBackupKind.World, settings.World, now, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Scheduled backup check failed.");
        }
    }

    private async Task RunDueRuleAsync(
        QuasarBackupKind kind,
        QuasarBackupRuleSettings rule,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        if (!rule.Enabled || !IsDue(rule, timestamp))
            return;

        try
        {
            await PerformBackupAsync(kind, rule, timestamp, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Scheduled {Kind} backup failed.", kind);
        }
    }

    private async Task<int> PerformBackupAsync(
        QuasarBackupKind kind,
        QuasarBackupRuleSettings rule,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var created = 0;
        switch (kind)
        {
            case QuasarBackupKind.Server:
                foreach (var server in _servers.GetServers())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _backupService.WriteServerBackupFileAsync(server.UniqueName, timestamp, automatic: true, cancellationToken);
                    _backupService.PruneAutomaticBackups(QuasarBackupKind.Server, rule.RetentionCount, server.UniqueName);
                    created++;
                }
                break;

            case QuasarBackupKind.World:
                foreach (var server in _servers.GetServers())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _backupService.WriteWorldBackupFileAsync(server.UniqueName, timestamp, automatic: true, cancellationToken);
                    _backupService.PruneAutomaticBackups(QuasarBackupKind.World, rule.RetentionCount, server.UniqueName);
                    created++;
                }
                break;

            case QuasarBackupKind.Configuration:
            default:
                await _backupService.WriteBackupFileAsync(timestamp, automatic: true, cancellationToken);
                _backupService.PruneAutomaticBackups(QuasarBackupKind.Configuration, rule.RetentionCount);
                created++;
                break;
        }

        await _settingsService.UpdateLastBackupAsync(kind, timestamp, cancellationToken);
        return created;
    }

    private static bool IsDue(QuasarBackupRuleSettings settings, DateTimeOffset now)
    {
        // First run after enabling happens at the next tick.
        if (settings.LastBackupUtc is not { } last)
            return true;

        var nextDue = ComputeNextDueLocal(last.ToLocalTime().DateTime, settings);
        return now.ToLocalTime().DateTime >= nextDue;
    }

    private static DateTime ComputeNextDueLocal(DateTime lastLocal, QuasarBackupRuleSettings settings)
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
