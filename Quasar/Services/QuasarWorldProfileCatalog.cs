using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services;

public sealed class QuasarWorldProfileCatalog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<QuasarWorldProfileCatalog> _logger;
    private List<QuasarWorldProfile> _profiles;
    private string _snapshot;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;

    public QuasarWorldProfileCatalog(ILogger<QuasarWorldProfileCatalog> logger)
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

    public IReadOnlyList<QuasarWorldProfile> GetProfiles()
    {
        lock (_sync)
        {
            return _profiles
                .Select(Clone)
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.WorldProfileId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public QuasarWorldProfile? GetProfile(string worldProfileId)
    {
        if (string.IsNullOrWhiteSpace(worldProfileId))
            return null;

        lock (_sync)
        {
            return _profiles
                .Where(p => string.Equals(p.WorldProfileId, worldProfileId, StringComparison.OrdinalIgnoreCase))
                .Select(Clone)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Returns the directory where this profile's world files are stored.
    /// Does not guarantee the directory exists.
    /// </summary>
    public string GetWorldDirectory(string worldProfileId) =>
        MagnetarPaths.GetQuasarWorldProfileWorldDirectory(worldProfileId);

    /// <summary>
    /// Creates a new world profile by copying files from <paramref name="sourcePath"/> into
    /// Quasar-managed storage.
    /// </summary>
    public async Task<QuasarWorldProfile> ImportAsync(
        string name,
        string description,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        sourcePath = sourcePath.Trim();

        if (!Directory.Exists(sourcePath))
            throw new InvalidOperationException($"Source world directory not found: {sourcePath}");

        var sandboxPath = Path.Combine(sourcePath, "Sandbox.sbc");
        if (!File.Exists(sandboxPath))
            throw new InvalidOperationException($"Source directory '{sourcePath}' does not contain Sandbox.sbc.");

        var profile = Normalize(new QuasarWorldProfile
        {
            Name = name,
            Description = description,
        });

        var destWorldDir = MagnetarPaths.GetQuasarWorldProfileWorldDirectory(profile.WorldProfileId);
        Directory.CreateDirectory(destWorldDir);

        foreach (var sourceFile in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourcePath, sourceFile);
            var destFile = Path.Combine(destWorldDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(sourceFile, destFile, overwrite: true);
        }

        lock (_sync)
        {
            _profiles.Add(Clone(profile));
            _snapshot = CreateSnapshot(_profiles);
        }

        await SaveProfileAsync(profile, cancellationToken);
        Changed?.Invoke();
        return Clone(profile);
    }

    public async Task DeleteAsync(string worldProfileId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(worldProfileId))
            return;

        QuasarWorldProfile? removed = null;
        lock (_sync)
        {
            var index = _profiles.FindIndex(p =>
                string.Equals(p.WorldProfileId, worldProfileId, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                removed = Clone(_profiles[index]);
                _profiles.RemoveAt(index);
                _snapshot = CreateSnapshot(_profiles);
            }
        }

        if (removed is null)
            return;

        await ArchiveAndDeleteProfileAsync(removed.WorldProfileId, cancellationToken);
        Changed?.Invoke();
    }

    private List<QuasarWorldProfile> LoadProfiles()
    {
        try
        {
            var directory = MagnetarPaths.GetQuasarWorldProfilesDirectory();
            if (!Directory.Exists(directory))
                return [];

            return Directory
                .GetFiles(directory, "profile.json", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(LoadProfile)
                .Where(p => p is not null)
                .Select(p => Normalize(p!))
                .ToList();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load Quasar world profiles.");
            return [];
        }
    }

    private QuasarWorldProfile? LoadProfile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<QuasarWorldProfile>(json, JsonOptions);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load Quasar world profile from {Path}", path);
            return null;
        }
    }

    private async Task SaveProfileAsync(QuasarWorldProfile profile, CancellationToken cancellationToken)
    {
        profile.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var path = MagnetarPaths.GetQuasarWorldProfileDefinitionPath(profile.WorldProfileId);
        var historyDirectory = MagnetarPaths.GetQuasarWorldProfileHistoryDirectory(profile.WorldProfileId);
        var json = JsonSerializer.Serialize(profile, JsonOptions);

        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);

        Directory.CreateDirectory(historyDirectory);
        var historyPath = Path.Combine(historyDirectory, $"{profile.UpdatedAtUtc:yyyyMMddHHmmssfff}.json");
        await AtomicFileWriter.WriteTextAsync(historyPath, json, cancellationToken);

        _logger.LogInformation("Saved Quasar world profile to {Path}", path);
    }

    private async Task ArchiveAndDeleteProfileAsync(string worldProfileId, CancellationToken cancellationToken)
    {
        var currentPath = MagnetarPaths.GetQuasarWorldProfileDefinitionPath(worldProfileId);
        if (File.Exists(currentPath))
        {
            var historyDirectory = MagnetarPaths.GetQuasarWorldProfileHistoryDirectory(worldProfileId);
            Directory.CreateDirectory(historyDirectory);

            var deletedContents = await File.ReadAllTextAsync(currentPath, cancellationToken);
            var historyPath = Path.Combine(historyDirectory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-deleted.json");
            await AtomicFileWriter.WriteTextAsync(historyPath, deletedContents, cancellationToken);

            File.Delete(currentPath);
        }

        var worldDir = MagnetarPaths.GetQuasarWorldProfileWorldDirectory(worldProfileId);
        if (Directory.Exists(worldDir))
            Directory.Delete(worldDir, recursive: true);

        _logger.LogInformation("Deleted Quasar world profile {WorldProfileId}", worldProfileId);
    }

    private static QuasarWorldProfile Normalize(QuasarWorldProfile profile)
    {
        profile.WorldProfileId = string.IsNullOrWhiteSpace(profile.WorldProfileId)
            ? Guid.NewGuid().ToString("N")
            : profile.WorldProfileId.Trim();
        profile.Name = profile.Name?.Trim() ?? string.Empty;
        profile.Description = profile.Description?.Trim() ?? string.Empty;
        if (profile.UpdatedAtUtc == default)
            profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
        return profile;
    }

    private static QuasarWorldProfile Clone(QuasarWorldProfile profile) =>
        new()
        {
            WorldProfileId = profile.WorldProfileId,
            Name = profile.Name,
            Description = profile.Description,
            UpdatedAtUtc = profile.UpdatedAtUtc,
        };

    private void StartWatching()
    {
        var directory = MagnetarPaths.GetQuasarWorldProfilesDirectory();
        Directory.CreateDirectory(directory);

        _watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
            Filter = "profile.json",
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
        List<QuasarWorldProfile> reloaded;
        string snapshot;

        try
        {
            reloaded = LoadProfiles();
            snapshot = CreateSnapshot(reloaded);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed reloading Quasar world profiles from disk.");
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

        _logger.LogInformation("Reloaded Quasar world profiles from disk after external edit.");
        Changed?.Invoke();
    }

    private static string CreateSnapshot(IEnumerable<QuasarWorldProfile> profiles)
    {
        var normalized = profiles
            .Select(p => Normalize(Clone(p)))
            .OrderBy(p => p.WorldProfileId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(normalized, JsonOptions);
    }
}
