using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services;

public sealed class QuasarConfigProfileCatalog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<QuasarConfigProfileCatalog> _logger;
    private List<QuasarConfigProfile> _profiles;
    private string _snapshot;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;

    public QuasarConfigProfileCatalog(ILogger<QuasarConfigProfileCatalog> logger)
    {
        _logger = logger;
        _profiles = LoadProfiles();
        _snapshot = CreateSnapshot(_profiles);
        StartWatching();
    }

    public event Action? Changed;

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
    }

    public IReadOnlyList<QuasarConfigProfile> GetProfiles()
    {
        lock (_sync)
        {
            return _profiles
                .Select(Clone)
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(profile => profile.ConfigProfileId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public QuasarConfigProfile? GetProfile(string configProfileId)
    {
        if (string.IsNullOrWhiteSpace(configProfileId))
            return null;

        lock (_sync)
        {
            return _profiles
                .Where(profile => string.Equals(profile.ConfigProfileId, configProfileId, StringComparison.OrdinalIgnoreCase))
                .Select(Clone)
                .FirstOrDefault();
        }
    }

    public async Task UpsertAsync(QuasarConfigProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var normalized = Normalize(Clone(profile));

        lock (_sync)
        {
            var index = _profiles.FindIndex(existing =>
                string.Equals(existing.ConfigProfileId, normalized.ConfigProfileId, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
                _profiles[index] = Clone(normalized);
            else
                _profiles.Add(Clone(normalized));

            _snapshot = CreateSnapshot(_profiles);
        }

        await SaveProfileAsync(normalized, cancellationToken);
        Changed?.Invoke();
    }

    public async Task DeleteAsync(string configProfileId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configProfileId))
            return;

        QuasarConfigProfile? removed = null;
        lock (_sync)
        {
            var index = _profiles.FindIndex(existing =>
                string.Equals(existing.ConfigProfileId, configProfileId, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                removed = Clone(_profiles[index]);
                _profiles.RemoveAt(index);
                _snapshot = CreateSnapshot(_profiles);
            }
        }

        if (removed is null)
            return;

        await ArchiveAndDeleteCurrentProfileAsync(removed.ConfigProfileId, cancellationToken);
        Changed?.Invoke();
    }

    private List<QuasarConfigProfile> LoadProfiles()
    {
        try
        {
            var directory = GetProfilesDirectory();
            if (!Directory.Exists(directory))
                return [];

            return Directory
                .GetFiles(directory, "profile.json", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(LoadProfile)
                .Where(profile => profile is not null)
                .Select(profile => Normalize(profile!))
                .ToList();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load Quasar config profiles.");
            return [];
        }
    }

    private QuasarConfigProfile? LoadProfile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<QuasarConfigProfile>(json, JsonOptions);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load Quasar config profile from {Path}", path);
            return null;
        }
    }

    private async Task SaveProfileAsync(QuasarConfigProfile profile, CancellationToken cancellationToken)
    {
        profile.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var path = GetProfilePath(profile.ConfigProfileId);
        var historyDirectory = GetProfileHistoryDirectory(profile.ConfigProfileId);
        var json = JsonSerializer.Serialize(profile, JsonOptions);

        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);

        Directory.CreateDirectory(historyDirectory);
        var historyPath = Path.Combine(historyDirectory, $"{profile.UpdatedAtUtc:yyyyMMddHHmmssfff}.json");
        await AtomicFileWriter.WriteTextAsync(historyPath, json, cancellationToken);

        _logger.LogInformation("Saved Quasar config profile to {Path}", path);
    }

    private async Task ArchiveAndDeleteCurrentProfileAsync(string configProfileId, CancellationToken cancellationToken)
    {
        var currentPath = GetProfilePath(configProfileId);
        if (!File.Exists(currentPath))
            return;

        var historyDirectory = GetProfileHistoryDirectory(configProfileId);
        Directory.CreateDirectory(historyDirectory);

        var deletedContents = await File.ReadAllTextAsync(currentPath, cancellationToken);
        var historyPath = Path.Combine(historyDirectory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-deleted.json");
        await AtomicFileWriter.WriteTextAsync(historyPath, deletedContents, cancellationToken);

        File.Delete(currentPath);
        _logger.LogInformation("Deleted active Quasar config profile at {Path}", currentPath);
    }

    private static QuasarConfigProfile Normalize(QuasarConfigProfile profile)
    {
        profile.ConfigProfileId = string.IsNullOrWhiteSpace(profile.ConfigProfileId)
            ? Guid.NewGuid().ToString("N")
            : profile.ConfigProfileId.Trim();
        profile.Name = profile.Name?.Trim() ?? string.Empty;
        profile.Description = profile.Description?.Trim() ?? string.Empty;
        profile.RootSettings ??= new QuasarWorldRootSettings();
        profile.SessionSettings ??= new QuasarSessionSettings();
        profile.RootSettings.Administrators = (profile.RootSettings.Administrators ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        profile.RootSettings.Reserved = (profile.RootSettings.Reserved ?? [])
            .Where(value => value > 0)
            .Distinct()
            .OrderBy(value => value)
            .ToList();
        profile.RootSettings.Banned = (profile.RootSettings.Banned ?? [])
            .Where(value => value > 0)
            .Distinct()
            .OrderBy(value => value)
            .ToList();
        profile.Plugins = (profile.Plugins ?? [])
            .Where(plugin => !string.IsNullOrWhiteSpace(plugin.PluginId))
            .Select(plugin => new QuasarPluginSelection
            {
                PluginId = plugin.PluginId.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(plugin.DisplayName) ? plugin.PluginId.Trim() : plugin.DisplayName.Trim(),
                SelectedVersion = plugin.SelectedVersion?.Trim() ?? string.Empty,
            })
            .Where(plugin => QuasarPluginCatalogService.IsManualSelectionAllowed(plugin.PluginId))
            .DistinctBy(plugin => plugin.PluginId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(plugin => plugin.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        profile.Mods = (profile.Mods ?? [])
            .Where(mod => mod.WorkshopId > 0)
            .Select(mod => new QuasarModSelection
            {
                WorkshopId = mod.WorkshopId,
                DisplayName = string.IsNullOrWhiteSpace(mod.DisplayName) ? mod.WorkshopId.ToString() : mod.DisplayName.Trim(),
            })
            .DistinctBy(mod => mod.WorkshopId)
            .OrderBy(mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mod => mod.WorkshopId)
            .ToList();
        if (profile.UpdatedAtUtc == default)
            profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
        return profile;
    }

    private static QuasarConfigProfile Clone(QuasarConfigProfile profile)
    {
        return JsonSerializer.Deserialize<QuasarConfigProfile>(
                   JsonSerializer.Serialize(profile, JsonOptions),
                   JsonOptions)
               ?? new QuasarConfigProfile();
    }

    private void StartWatching()
    {
        var directory = GetProfilesDirectory();
        Directory.CreateDirectory(directory);

        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
            Filter = "*.json",
        };

        _watcher.Changed += HandleWatchedFileChanged;
        _watcher.Created += HandleWatchedFileChanged;
        _watcher.Deleted += HandleWatchedFileChanged;
        _watcher.Renamed += HandleWatchedFileChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void HandleWatchedFileChanged(object sender, FileSystemEventArgs args)
    {
        if (!IsTrackedProfilePath(args.FullPath))
            return;

        ScheduleReload();
    }

    private static bool IsTrackedProfilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return string.Equals(Path.GetFileName(path), "profile.json", StringComparison.OrdinalIgnoreCase);
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
        List<QuasarConfigProfile> reloaded;
        string snapshot;

        try
        {
            reloaded = LoadProfiles();
            snapshot = CreateSnapshot(reloaded);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed reloading Quasar config profiles from disk.");
            return;
        }

        var changed = false;
        lock (_sync)
        {
            if (!string.Equals(_snapshot, snapshot, StringComparison.Ordinal))
            {
                _profiles = reloaded;
                _snapshot = snapshot;
                changed = true;
            }
        }

        if (!changed)
            return;

        _logger.LogInformation("Reloaded Quasar config profiles from disk after external edit.");
        Changed?.Invoke();
    }

    private static string CreateSnapshot(IEnumerable<QuasarConfigProfile> profiles)
    {
        var normalized = profiles
            .Select(profile => Normalize(Clone(profile)))
            .OrderBy(profile => profile.ConfigProfileId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static string GetProfilesDirectory() =>
        Path.Combine(MagnetarPaths.GetQuasarDirectory(), "ConfigProfiles");

    private static string GetProfileDirectory(string configProfileId) =>
        Path.Combine(GetProfilesDirectory(), configProfileId);

    private static string GetProfilePath(string configProfileId) =>
        Path.Combine(GetProfileDirectory(configProfileId), "profile.json");

    private static string GetProfileHistoryDirectory(string configProfileId) =>
        Path.Combine(GetProfileDirectory(configProfileId), "History");
}
