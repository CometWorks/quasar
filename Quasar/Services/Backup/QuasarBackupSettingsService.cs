using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services.Backup;

/// <summary>
/// Singleton store for the automatic-backup schedule settings. Persists to
/// <c>backup-settings.json</c> in the Quasar data directory and picks up external
/// edits via a debounced file watch, mirroring <see cref="BrandingService"/>.
/// </summary>
public sealed class QuasarBackupSettingsService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _sync = new();
    private readonly ILogger<QuasarBackupSettingsService> _logger;
    private QuasarBackupSettings _settings;
    private string _snapshot;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;

    public QuasarBackupSettingsService(ILogger<QuasarBackupSettingsService> logger)
    {
        _logger = logger;
        _settings = LoadSettings();
        _snapshot = CreateSnapshot(_settings);
        StartWatching();
    }

    public event Action? Changed;

    /// <summary>Returns a deep copy safe for UI draft editing.</summary>
    public QuasarBackupSettings GetSettings()
    {
        lock (_sync)
            return _settings.Clone();
    }

    public async Task SaveAsync(QuasarBackupSettings settings, CancellationToken cancellationToken = default)
    {
        // Preserve the scheduler's own LastBackupUtc bookkeeping across UI saves.
        var normalized = QuasarBackupSettings.Normalize(settings);
        lock (_sync)
        {
            normalized.Configuration.LastBackupUtc ??= _settings.Configuration.LastBackupUtc;
            normalized.Server.LastBackupUtc ??= _settings.Server.LastBackupUtc;
            normalized.World.LastBackupUtc ??= _settings.World.LastBackupUtc;
        }

        await PersistAsync(normalized, cancellationToken);
        Changed?.Invoke();
    }

    /// <summary>Records the timestamp of the most recent automatic backup.</summary>
    public async Task UpdateLastBackupAsync(
        QuasarBackupKind kind,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        QuasarBackupSettings updated;
        lock (_sync)
        {
            updated = _settings.Clone();
            updated.GetRule(kind).LastBackupUtc = timestamp;
        }

        await PersistAsync(QuasarBackupSettings.Normalize(updated), cancellationToken);
    }

    private async Task PersistAsync(QuasarBackupSettings normalized, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        var path = MagnetarPaths.GetQuasarBackupSettingsPath();

        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);

        lock (_sync)
        {
            _settings = normalized;
            _snapshot = json;
        }

        _logger.LogInformation("Saved backup settings to {Path}", path);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
    }

    private QuasarBackupSettings LoadSettings()
    {
        var path = MagnetarPaths.GetQuasarBackupSettingsPath();
        try
        {
            if (!File.Exists(path))
                return QuasarBackupSettings.Normalize(null);

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<QuasarBackupSettings>(json, JsonOptions);
            return QuasarBackupSettings.Normalize(settings);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed loading backup settings from {Path}", path);
            return QuasarBackupSettings.Normalize(null);
        }
    }

    private void StartWatching()
    {
        var path = MagnetarPaths.GetQuasarBackupSettingsPath();
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        Directory.CreateDirectory(directory);

        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
            Filter = Path.GetFileName(path),
        };

        _watcher.Changed += HandleWatchedFileChanged;
        _watcher.Created += HandleWatchedFileChanged;
        _watcher.Deleted += HandleWatchedFileChanged;
        _watcher.Renamed += HandleWatchedFileChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void HandleWatchedFileChanged(object sender, FileSystemEventArgs args) => ScheduleReload();

    private void ScheduleReload()
    {
        CancellationTokenSource debounce;
        lock (_sync)
        {
            _reloadDebounce?.Cancel();
            _reloadDebounce?.Dispose();
            _reloadDebounce = new CancellationTokenSource();
            debounce = _reloadDebounce;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), debounce.Token);
                ReloadFromDisk();
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private void ReloadFromDisk()
    {
        QuasarBackupSettings reloaded;
        string snapshot;
        try
        {
            reloaded = LoadSettings();
            snapshot = CreateSnapshot(reloaded);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed reloading backup settings from disk.");
            return;
        }

        var changed = false;
        lock (_sync)
        {
            if (!string.Equals(_snapshot, snapshot, StringComparison.Ordinal))
            {
                _settings = reloaded;
                _snapshot = snapshot;
                changed = true;
            }
        }

        if (!changed)
            return;

        _logger.LogInformation("Reloaded backup settings from disk after external edit.");
        Changed?.Invoke();
    }

    private static string CreateSnapshot(QuasarBackupSettings settings) =>
        JsonSerializer.Serialize(QuasarBackupSettings.Normalize(settings), JsonOptions);
}
