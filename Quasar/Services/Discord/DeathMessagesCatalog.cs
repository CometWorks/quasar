using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;

namespace Quasar.Services.Discord;

public sealed class DeathMessagesCatalog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<DeathMessagesCatalog> _logger;
    private DeathMessagesConfig _config;
    private string _snapshot;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;

    public DeathMessagesCatalog(ILogger<DeathMessagesCatalog> logger)
    {
        _logger = logger;
        _config = LoadOrCreateConfig();
        _snapshot = CreateSnapshot(_config);
        StartWatching();
    }

    public event Action? Changed;

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
    }

    public DeathMessagesConfig GetConfig()
    {
        lock (_sync)
        {
            return _config.Clone();
        }
    }

    public async Task SaveAsync(DeathMessagesConfig config, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(config);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        var path = MagnetarPaths.GetQuasarDeathMessagesPath();

        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);

        lock (_sync)
        {
            _config = normalized.Clone();
            _snapshot = json;
        }

        Changed?.Invoke();
    }

    public Task ResetAsync(CancellationToken cancellationToken = default)
    {
        return SaveAsync(DeathMessagesConfig.CreateDefault(), cancellationToken);
    }

    private DeathMessagesConfig LoadOrCreateConfig()
    {
        var path = MagnetarPaths.GetQuasarDeathMessagesPath();

        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<DeathMessagesConfig>(json, JsonOptions);
                return Normalize(loaded);
            }

            var created = Normalize(DeathMessagesConfig.CreateDefault());
            var serialized = JsonSerializer.Serialize(created, JsonOptions);
            AtomicFileWriter.WriteTextAsync(path, serialized).GetAwaiter().GetResult();
            return created;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed loading death messages config from {Path}", path);
            return Normalize(DeathMessagesConfig.CreateDefault());
        }
    }

    private void StartWatching()
    {
        var path = MagnetarPaths.GetQuasarDeathMessagesPath();
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

    private void HandleWatchedFileChanged(object sender, FileSystemEventArgs args)
    {
        if (!IsTrackedPath(args.FullPath))
            return;

        ScheduleReload();
    }

    private bool IsTrackedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return string.Equals(
            Path.GetFullPath(path),
            Path.GetFullPath(MagnetarPaths.GetQuasarDeathMessagesPath()),
            StringComparison.OrdinalIgnoreCase);
    }

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
        DeathMessagesConfig reloaded;
        string snapshot;

        try
        {
            reloaded = LoadOrCreateConfig();
            snapshot = CreateSnapshot(reloaded);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed reloading death messages config from disk.");
            return;
        }

        var changed = false;
        lock (_sync)
        {
            if (!string.Equals(_snapshot, snapshot, StringComparison.Ordinal))
            {
                _config = reloaded;
                _snapshot = snapshot;
                changed = true;
            }
        }

        if (changed)
            Changed?.Invoke();
    }

    private static DeathMessagesConfig Normalize(DeathMessagesConfig? config)
    {
        config ??= DeathMessagesConfig.CreateDefault();
        var defaults = DeathMessagesConfig.CreateDefault();

        return new DeathMessagesConfig
        {
            SuicideMessages = NormalizeList(config.SuicideMessages, defaults.SuicideMessages),
            PvPMessages = NormalizeList(config.PvPMessages, defaults.PvPMessages),
            TurretMessages = NormalizeList(config.TurretMessages, defaults.TurretMessages),
            GridMessages = NormalizeList(config.GridMessages, defaults.GridMessages),
            OxygenMessages = NormalizeList(config.OxygenMessages, defaults.OxygenMessages),
            PressureMessages = NormalizeList(config.PressureMessages, defaults.PressureMessages),
            CollisionMessages = NormalizeList(config.CollisionMessages, defaults.CollisionMessages),
            AccidentMessages = NormalizeList(config.AccidentMessages, defaults.AccidentMessages),
        };
    }

    private static List<string> NormalizeList(List<string>? values, List<string> fallback)
    {
        var normalized = (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToList();

        return normalized.Count > 0 ? normalized : [.. fallback];
    }

    private static string CreateSnapshot(DeathMessagesConfig config)
    {
        return JsonSerializer.Serialize(Normalize(config), JsonOptions);
    }
}
