using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly SemaphoreSlim _appSettingsGate = new(1, 1);
    private readonly ILogger<QuasarBackupSettingsService> _logger;
    private readonly WebServiceOptions _options;
    private QuasarBackupSettings _settings;
    private string _snapshot;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;

    public QuasarBackupSettingsService(
        ILogger<QuasarBackupSettingsService> logger,
        WebServiceOptions options)
    {
        _logger = logger;
        _options = options;
        _settings = LoadSettings();
        _snapshot = CreateSnapshot(_settings);
        StartWatching();
    }

    public event Action? Changed;

    public event Action? BackupDirectoryChanged;

    public string AppSettingsPath => Path.Combine(MagnetarPaths.GetQuasarDirectory(), "appsettings.json");

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

    public QuasarBackupDirectorySettings GetBackupDirectorySettings()
    {
        var configuredDirectory = ReadConfiguredBackupDirectory();
        var environmentDirectory = Environment.GetEnvironmentVariable("QUASAR_BACKUP_DIR") ?? string.Empty;
        return new QuasarBackupDirectorySettings
        {
            ConfiguredDirectory = configuredDirectory,
            ResolvedDirectory = _options.BackupDirectory,
            AppSettingsPath = AppSettingsPath,
            EnvironmentOverride = !string.IsNullOrWhiteSpace(environmentDirectory),
            EnvironmentDirectory = environmentDirectory,
        };
    }

    public async Task<QuasarBackupDirectorySettings> SaveBackupDirectoryAsync(
        string? configuredDirectory,
        CancellationToken cancellationToken = default)
    {
        var environmentDirectory = Environment.GetEnvironmentVariable("QUASAR_BACKUP_DIR");
        if (!string.IsNullOrWhiteSpace(environmentDirectory))
            throw new InvalidOperationException("QUASAR_BACKUP_DIR is set; remove it before editing the backup folder from the web UI.");

        var normalizedDirectory = configuredDirectory?.Trim() ?? string.Empty;
        var resolvedDirectory = WebServiceOptions.ResolveBackupDirectory(normalizedDirectory);
        Directory.CreateDirectory(resolvedDirectory);

        await _appSettingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var root = await ReadAppSettingsAsync(cancellationToken).ConfigureAwait(false);
            var quasar = GetOrCreateObject(root, "Quasar");
            quasar["BackupDirectory"] = normalizedDirectory;

            await AtomicFileWriter.WriteTextAsync(
                    AppSettingsPath,
                    root.ToJsonString(JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);

            _options.BackupDirectory = resolvedDirectory;
        }
        finally
        {
            _appSettingsGate.Release();
        }

        _logger.LogInformation("Saved backup directory setting to {Path}", AppSettingsPath);
        BackupDirectoryChanged?.Invoke();
        return GetBackupDirectorySettings();
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

    private string ReadConfiguredBackupDirectory()
    {
        try
        {
            if (!File.Exists(AppSettingsPath))
                return string.Empty;

            var text = File.ReadAllText(AppSettingsPath);
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var root = JsonNode.Parse(text)?.AsObject();
            return root?["Quasar"]?["BackupDirectory"]?.GetValue<string>()?.Trim() ?? string.Empty;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed reading backup directory setting from {Path}", AppSettingsPath);
            return string.Empty;
        }
    }

    private async Task<JsonObject> ReadAppSettingsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(AppSettingsPath))
            return new JsonObject();

        var text = await File.ReadAllTextAsync(AppSettingsPath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
            return new JsonObject();

        return JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
            return existing;

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }
}

public sealed class QuasarBackupDirectorySettings
{
    public string ConfiguredDirectory { get; set; } = string.Empty;

    public string ResolvedDirectory { get; set; } = string.Empty;

    public string AppSettingsPath { get; set; } = string.Empty;

    public bool EnvironmentOverride { get; set; }

    public string EnvironmentDirectory { get; set; } = string.Empty;
}
