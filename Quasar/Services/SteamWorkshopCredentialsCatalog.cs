using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;

namespace Quasar.Services;

public sealed class SteamWorkshopCredentialsCatalog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<SteamWorkshopCredentialsCatalog> _logger;
    private SteamWorkshopCredentials _credentials;
    private string _snapshot;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;

    public SteamWorkshopCredentialsCatalog(ILogger<SteamWorkshopCredentialsCatalog> logger)
    {
        _logger = logger;
        _credentials = LoadCredentials();
        _snapshot = CreateSnapshot(_credentials);
        StartWatching();
    }

    public event Action? Changed;

    public bool HasWebApiKey
    {
        get
        {
            lock (_sync)
            {
                return !string.IsNullOrWhiteSpace(_credentials.WebApiKey);
            }
        }
    }

    public SteamWorkshopCredentials GetCredentials()
    {
        lock (_sync)
        {
            return _credentials.Clone();
        }
    }

    public async Task SaveAsync(SteamWorkshopCredentials credentials, CancellationToken cancellationToken = default)
    {
        var normalized = SteamWorkshopCredentials.Normalize(credentials);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        var path = MagnetarPaths.GetQuasarWorkshopOptionsPath();

        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);

        lock (_sync)
        {
            _credentials = normalized.Clone();
            _snapshot = json;
        }

        _logger.LogInformation("Saved Steam Workshop credentials to {Path}", path);
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
    }

    private SteamWorkshopCredentials LoadCredentials()
    {
        var path = MagnetarPaths.GetQuasarWorkshopOptionsPath();

        try
        {
            if (!File.Exists(path))
                return SteamWorkshopCredentials.Normalize(null);

            var json = File.ReadAllText(path);
            var credentials = JsonSerializer.Deserialize<SteamWorkshopCredentials>(json, JsonOptions);
            return SteamWorkshopCredentials.Normalize(credentials);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed loading Steam Workshop credentials from {Path}", path);
            return SteamWorkshopCredentials.Normalize(null);
        }
    }

    private void StartWatching()
    {
        var path = MagnetarPaths.GetQuasarWorkshopOptionsPath();
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
        if (!string.Equals(
                Path.GetFullPath(args.FullPath),
                Path.GetFullPath(MagnetarPaths.GetQuasarWorkshopOptionsPath()),
                StringComparison.OrdinalIgnoreCase))
            return;

        ScheduleReload();
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
        var reloaded = LoadCredentials();
        var snapshot = CreateSnapshot(reloaded);
        var changed = false;

        lock (_sync)
        {
            if (!string.Equals(_snapshot, snapshot, StringComparison.Ordinal))
            {
                _credentials = reloaded;
                _snapshot = snapshot;
                changed = true;
            }
        }

        if (!changed)
            return;

        _logger.LogInformation("Reloaded Steam Workshop credentials from disk after external edit.");
        Changed?.Invoke();
    }

    private static string CreateSnapshot(SteamWorkshopCredentials credentials) =>
        JsonSerializer.Serialize(SteamWorkshopCredentials.Normalize(credentials), JsonOptions);
}

public sealed class SteamWorkshopCredentials
{
    public string WebApiKey { get; set; } = string.Empty;

    public SteamWorkshopCredentials Clone() => new()
    {
        WebApiKey = WebApiKey,
    };

    public static SteamWorkshopCredentials Normalize(SteamWorkshopCredentials? credentials) => new()
    {
        WebApiKey = credentials?.WebApiKey?.Trim() ?? string.Empty,
    };
}
