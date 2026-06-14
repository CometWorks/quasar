using System.Threading.Channels;
using Quasar.Models;

namespace Quasar.Services.Backup;

public enum QueuedBackupJobKind
{
    EnabledRules,
    Server,
    World,
}

public sealed record QueuedBackupJobResult(
    Guid Id,
    QueuedBackupJobKind Kind,
    string? ServerUniqueName,
    bool Success,
    int CreatedCount,
    string Message,
    Exception? Exception = null);

internal sealed record QueuedBackupJob(Guid Id, QueuedBackupJobKind Kind, string? ServerUniqueName);

/// <summary>
/// Background scheduler and manual queue for stored backup creation. Work is
/// serialized so long-running ZIP creation does not run on the Blazor circuit.
/// </summary>
public sealed class AutomaticBackupService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    private readonly QuasarBackupService _backupService;
    private readonly QuasarBackupSettingsService _settingsService;
    private readonly DedicatedServerCatalog _servers;
    private readonly ILogger<AutomaticBackupService> _logger;
    private readonly Channel<QueuedBackupJob> _queue = Channel.CreateUnbounded<QueuedBackupJob>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly SemaphoreSlim _backupGate = new(1, 1);

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

    public event Action<QueuedBackupJobResult>? QueuedBackupCompleted;

    /// <summary>Queues all enabled automatic-backup rules to run in the background.</summary>
    public Guid QueueEnabledBackupsNow() =>
        QueueBackup(QueuedBackupJobKind.EnabledRules);

    /// <summary>Queues a server-scope backup for one configured server.</summary>
    public Guid QueueServerBackup(string uniqueName) =>
        QueueBackup(QueuedBackupJobKind.Server, uniqueName);

    /// <summary>Queues a world-scope backup for one configured server.</summary>
    public Guid QueueWorldBackup(string uniqueName) =>
        QueueBackup(QueuedBackupJobKind.World, uniqueName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _backupService.CleanupIncompleteBackupFiles();

        var scheduler = RunSchedulerAsync(stoppingToken);
        var queue = RunQueueAsync(stoppingToken);
        await Task.WhenAll(scheduler, queue);
    }

    /// <summary>
    /// Performs all enabled automatic-backup rules immediately, regardless of
    /// schedule. The scheduler and manual queue share this path.
    /// </summary>
    public async Task<int> RunEnabledBackupsNowAsync(CancellationToken cancellationToken = default)
    {
        await _backupGate.WaitAsync(cancellationToken);
        try
        {
            return await RunEnabledBackupsNowCoreAsync(cancellationToken);
        }
        finally
        {
            _backupGate.Release();
        }
    }

    private Guid QueueBackup(QueuedBackupJobKind kind, string? uniqueName = null)
    {
        var id = Guid.NewGuid();
        if (!_queue.Writer.TryWrite(new QueuedBackupJob(id, kind, uniqueName)))
            throw new InvalidOperationException("Backup queue is not accepting new work.");

        return id;
    }

    private async Task RunSchedulerAsync(CancellationToken stoppingToken)
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

    private async Task RunQueueAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
                await RunQueuedBackupAsync(job, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RunQueuedBackupAsync(QueuedBackupJob job, CancellationToken cancellationToken)
    {
        try
        {
            var created = job.Kind switch
            {
                QueuedBackupJobKind.Server => await RunSingleServerBackupAsync(job, QuasarBackupKind.Server, cancellationToken),
                QueuedBackupJobKind.World => await RunSingleServerBackupAsync(job, QuasarBackupKind.World, cancellationToken),
                _ => await RunEnabledBackupsNowAsync(cancellationToken),
            };

            QueuedBackupCompleted?.Invoke(new QueuedBackupJobResult(
                job.Id,
                job.Kind,
                job.ServerUniqueName,
                Success: true,
                CreatedCount: created,
                Message: CreateSuccessMessage(job, created)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Queued {Kind} backup failed.", job.Kind);
            QueuedBackupCompleted?.Invoke(new QueuedBackupJobResult(
                job.Id,
                job.Kind,
                job.ServerUniqueName,
                Success: false,
                CreatedCount: 0,
                Message: CreateFailureMessage(job, exception),
                Exception: exception));
        }
    }

    private async Task<int> RunEnabledBackupsNowCoreAsync(CancellationToken cancellationToken)
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

    private async Task<int> RunSingleServerBackupAsync(
        QueuedBackupJob job,
        QuasarBackupKind kind,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.ServerUniqueName))
            throw new InvalidOperationException("Backup target server was not specified.");

        await _backupGate.WaitAsync(cancellationToken);
        try
        {
            var timestamp = DateTimeOffset.Now;
            if (kind == QuasarBackupKind.World)
                await _backupService.WriteWorldBackupFileAsync(job.ServerUniqueName, timestamp, cancellationToken: cancellationToken);
            else
                await _backupService.WriteServerBackupFileAsync(job.ServerUniqueName, timestamp, cancellationToken: cancellationToken);

            return 1;
        }
        finally
        {
            _backupGate.Release();
        }
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
            await _backupGate.WaitAsync(cancellationToken);
            try
            {
                await PerformBackupAsync(kind, rule, timestamp, cancellationToken);
            }
            finally
            {
                _backupGate.Release();
            }
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

    private static string CreateSuccessMessage(QueuedBackupJob job, int created) =>
        job.Kind switch
        {
            QueuedBackupJobKind.Server => "Server backup created in the Backups folder.",
            QueuedBackupJobKind.World => "World backup created in the Backups folder.",
            _ when created == 0 => "No enabled automatic backup rules created a backup.",
            _ => $"Created {created} backup(s) in the Backups folder.",
        };

    private static string CreateFailureMessage(QueuedBackupJob job, Exception exception) =>
        job.Kind switch
        {
            QueuedBackupJobKind.Server => $"Server backup failed: {exception.Message}",
            QueuedBackupJobKind.World => $"World backup failed: {exception.Message}",
            _ => $"Automatic backup failed: {exception.Message}",
        };

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
