using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Quasar.Services;

namespace Quasar.Services.Auth;

public sealed class RbacConfigCatalog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<RbacConfigCatalog> _logger;
    private RbacConfig _config;
    private string _snapshot;
    private DebouncedFileWatcher? _watcher;

    public RbacConfigCatalog(ILogger<RbacConfigCatalog> logger)
    {
        _logger = logger;
        _config = LoadConfig();
        _snapshot = CreateSnapshot(_config);
        StartWatching();
    }

    public event Action? Changed;

    public void Dispose()
    {
        _watcher?.Dispose();
    }

    public RbacConfig GetConfig()
    {
        lock (_sync)
        {
            return _config.Clone();
        }
    }

    public async Task SaveAsync(RbacConfig config, CancellationToken cancellationToken = default)
    {
        var normalized = RbacConfig.Normalize(config);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        var path = GetPath();

        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);

        lock (_sync)
        {
            _config = normalized.Clone();
            _snapshot = json;
        }

        _logger.LogInformation("Saved RBAC config to {Path}", path);
        Changed?.Invoke();
    }

    public IReadOnlyList<string> GetSubjectRoles(string provider, string subject)
    {
        lock (_sync)
        {
            return _config.SubjectRoleMappings
                .Where(mapping =>
                    string.Equals(mapping.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(mapping.Subject, subject, StringComparison.OrdinalIgnoreCase))
                .SelectMany(mapping => mapping.Roles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private RbacConfig LoadConfig()
    {
        var path = GetPath();

        try
        {
            if (!File.Exists(path))
                return RbacConfig.Normalize(null);

            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<RbacConfig>(json, JsonOptions);
            return RbacConfig.Normalize(config);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed loading RBAC config from {Path}", path);
            return RbacConfig.Normalize(null);
        }
    }

    private void StartWatching()
    {
        _watcher = DebouncedFileWatcher.WatchFile(GetPath(), ReloadFromDisk);
    }

    private void ReloadFromDisk()
    {
        RbacConfig reloaded;
        string snapshot;

        try
        {
            reloaded = LoadConfig();
            snapshot = CreateSnapshot(reloaded);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed reloading RBAC config from disk.");
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

        if (!changed)
            return;

        _logger.LogInformation("Reloaded RBAC config from disk after external edit.");
        Changed?.Invoke();
    }

    private static string CreateSnapshot(RbacConfig config) =>
        JsonSerializer.Serialize(RbacConfig.Normalize(config), JsonOptions);

    private static string GetPath() =>
        Path.Combine(MagnetarPaths.GetQuasarDirectory(), "rbac.json");
}
