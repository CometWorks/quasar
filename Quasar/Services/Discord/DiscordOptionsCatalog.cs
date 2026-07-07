using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Quasar.Services;

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
    private DebouncedFileWatcher? _watcher;

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
        _watcher = DebouncedFileWatcher.WatchFile(MagnetarPaths.GetQuasarDiscordOptionsPath(), ReloadFromDisk);
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
