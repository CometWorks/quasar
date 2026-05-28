using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;

namespace Quasar.Services.Discord;

public sealed class DiscordOptionsCatalog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<DiscordOptionsCatalog> _logger;
    private DiscordOptions _options;
    private string _snapshot;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;

    public DiscordOptionsCatalog(ILogger<DiscordOptionsCatalog> logger)
    {
        _logger = logger;
        _options = LoadOptions();
        _snapshot = CreateSnapshot(_options);
        StartWatching();
    }

    public event Action? Changed;

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
    }

    public DiscordOptions GetOptions()
    {
        lock (_sync)
        {
            return _options.Clone();
        }
    }

    public async Task SaveAsync(DiscordOptions options, CancellationToken cancellationToken = default)
    {
        var normalized = DiscordOptions.Normalize(options);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        var path = MagnetarPaths.GetQuasarDiscordOptionsPath();

        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);

        lock (_sync)
        {
            _options = normalized.Clone();
            _snapshot = json;
        }

        _logger.LogInformation("Saved Discord options to {Path}", path);
        Changed?.Invoke();
    }

    private DiscordOptions LoadOptions()
    {
        var path = MagnetarPaths.GetQuasarDiscordOptionsPath();

        try
        {
            if (!File.Exists(path))
                return DiscordOptions.Normalize(null);

            var json = File.ReadAllText(path);
            var options = JsonSerializer.Deserialize<DiscordOptions>(json, JsonOptions);
            return DiscordOptions.Normalize(options);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed loading Discord options from {Path}", path);
            return DiscordOptions.Normalize(null);
        }
    }

    private void StartWatching()
    {
        var path = MagnetarPaths.GetQuasarDiscordOptionsPath();
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
            Path.GetFullPath(MagnetarPaths.GetQuasarDiscordOptionsPath()),
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
        DiscordOptions reloaded;
        string snapshot;

        try
        {
            reloaded = LoadOptions();
            snapshot = CreateSnapshot(reloaded);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed reloading Discord options from disk.");
            return;
        }

        var changed = false;
        lock (_sync)
        {
            if (!string.Equals(_snapshot, snapshot, StringComparison.Ordinal))
            {
                _options = reloaded;
                _snapshot = snapshot;
                changed = true;
            }
        }

        if (!changed)
            return;

        _logger.LogInformation("Reloaded Discord options from disk after external edit.");
        Changed?.Invoke();
    }

    private static string CreateSnapshot(DiscordOptions options)
    {
        return JsonSerializer.Serialize(DiscordOptions.Normalize(options), JsonOptions);
    }
}
