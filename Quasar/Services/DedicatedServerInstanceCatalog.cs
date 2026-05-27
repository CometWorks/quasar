using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services;

public sealed class DedicatedServerInstanceCatalog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly ILogger<DedicatedServerInstanceCatalog> _logger;
    private List<DedicatedServerInstanceDefinition> _instances;
    private string _snapshot;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;

    public DedicatedServerInstanceCatalog(ILogger<DedicatedServerInstanceCatalog> logger)
    {
        _logger = logger;
        _instances = LoadInstances();
        _snapshot = CreateSnapshot(_instances);
        StartWatching();
    }

    public event Action? Changed;

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
    }

    public IReadOnlyList<DedicatedServerInstanceDefinition> GetInstances()
    {
        lock (_sync)
        {
            return _instances
                .Select(Clone)
                .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(instance => instance.InstanceId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public DedicatedServerInstanceDefinition? GetInstance(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return null;

        lock (_sync)
        {
            return _instances
                .Where(instance => string.Equals(instance.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
                .Select(Clone)
                .FirstOrDefault();
        }
    }

    public async Task UpsertAsync(DedicatedServerInstanceDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var normalized = Normalize(Clone(definition));

        lock (_sync)
        {
            var index = _instances.FindIndex(instance =>
                string.Equals(instance.InstanceId, normalized.InstanceId, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
                _instances[index] = Clone(normalized);
            else
                _instances.Add(Clone(normalized));

            _snapshot = CreateSnapshot(_instances);
        }

        await SaveInstanceAsync(normalized, cancellationToken);
        Changed?.Invoke();
    }

    public async Task SetGoalStateAsync(
        string instanceId,
        DedicatedServerInstanceGoalState goalState,
        CancellationToken cancellationToken = default)
    {
        var definition = GetInstance(instanceId);
        if (definition is null)
            throw new InvalidOperationException($"Unknown Quasar instance '{instanceId}'.");

        definition.GoalState = goalState;
        definition.AutoStart = goalState == DedicatedServerInstanceGoalState.On;
        await UpsertAsync(definition, cancellationToken);
    }

    public async Task DeleteAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return;

        DedicatedServerInstanceDefinition? removed = null;

        lock (_sync)
        {
            var index = _instances.FindIndex(instance =>
                string.Equals(instance.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                removed = Clone(_instances[index]);
                _instances.RemoveAt(index);
                _snapshot = CreateSnapshot(_instances);
            }
        }

        if (removed is null)
            return;

        await ArchiveAndDeleteCurrentDefinitionAsync(removed.InstanceId, cancellationToken);
        Changed?.Invoke();
    }

    private List<DedicatedServerInstanceDefinition> LoadInstances()
    {
        try
        {
            var instancesDirectory = MagnetarPaths.GetQuasarInstancesDirectory();
            if (Directory.Exists(instancesDirectory))
            {
                var instancePaths = Directory.GetFiles(instancesDirectory, "instance.json", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (instancePaths.Count > 0)
                {
                    return instancePaths
                        .Select(LoadInstanceDefinition)
                        .Where(instance => instance is not null)
                        .Select(instance => Normalize(instance!))
                        .ToList();
                }
            }

            return LoadLegacyInstances();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load Quasar instance catalog.");
            return new List<DedicatedServerInstanceDefinition>();
        }
    }

    private List<DedicatedServerInstanceDefinition> LoadLegacyInstances()
    {
        var path = MagnetarPaths.GetQuasarLegacyInstancesPath();
        try
        {
            if (!File.Exists(path))
                return new List<DedicatedServerInstanceDefinition>();

            var json = File.ReadAllText(path);
            var instances = JsonSerializer.Deserialize<List<DedicatedServerInstanceDefinition>>(json, JsonOptions)
                            ?? new List<DedicatedServerInstanceDefinition>();

            _logger.LogInformation("Loaded legacy Quasar instance catalog from {Path}", path);
            return instances.Select(Normalize).ToList();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load legacy Quasar instance catalog from {Path}", path);
            return new List<DedicatedServerInstanceDefinition>();
        }
    }

    private DedicatedServerInstanceDefinition? LoadInstanceDefinition(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<DedicatedServerInstanceDefinition>(json, JsonOptions);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load Quasar instance definition from {Path}", path);
            return null;
        }
    }

    private async Task SaveInstanceAsync(DedicatedServerInstanceDefinition definition, CancellationToken cancellationToken)
    {
        definition.UpdatedAtUtc = DateTimeOffset.UtcNow;

        var path = MagnetarPaths.GetQuasarInstanceDefinitionPath(definition.InstanceId);
        var historyDirectory = MagnetarPaths.GetQuasarInstanceHistoryDirectory(definition.InstanceId);
        var json = JsonSerializer.Serialize(definition, JsonOptions);

        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);

        Directory.CreateDirectory(historyDirectory);
        var historyPath = Path.Combine(historyDirectory, $"{definition.UpdatedAtUtc:yyyyMMddHHmmssfff}.json");
        await AtomicFileWriter.WriteTextAsync(historyPath, json, cancellationToken);

        _logger.LogInformation("Saved Quasar instance definition to {Path}", path);
    }

    private async Task ArchiveAndDeleteCurrentDefinitionAsync(string instanceId, CancellationToken cancellationToken)
    {
        var currentPath = MagnetarPaths.GetQuasarInstanceDefinitionPath(instanceId);
        if (!File.Exists(currentPath))
            return;

        var historyDirectory = MagnetarPaths.GetQuasarInstanceHistoryDirectory(instanceId);
        Directory.CreateDirectory(historyDirectory);

        var deletedContents = await File.ReadAllTextAsync(currentPath, cancellationToken);
        var historyPath = Path.Combine(historyDirectory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-deleted.json");
        await AtomicFileWriter.WriteTextAsync(historyPath, deletedContents, cancellationToken);

        File.Delete(currentPath);
        _logger.LogInformation("Deleted active Quasar instance definition at {Path}", currentPath);
    }

    private static DedicatedServerInstanceDefinition Normalize(DedicatedServerInstanceDefinition instance)
    {
        instance.InstanceId = string.IsNullOrWhiteSpace(instance.InstanceId)
            ? Guid.NewGuid().ToString("N")
            : instance.InstanceId.Trim();
        instance.Name = instance.Name?.Trim() ?? string.Empty;
        instance.ExecutablePath = instance.ExecutablePath?.Trim() ?? string.Empty;
        instance.WorkingDirectory = instance.WorkingDirectory?.Trim() ?? string.Empty;
        instance.DedicatedServerAppDataPath = string.IsNullOrWhiteSpace(instance.DedicatedServerAppDataPath)
            ? MagnetarPaths.GetQuasarInstanceDedicatedServerAppDataDirectory(instance.InstanceId)
            : instance.DedicatedServerAppDataPath.Trim();
        instance.MagnetarAppDataPath = string.IsNullOrWhiteSpace(instance.MagnetarAppDataPath)
            ? MagnetarPaths.GetQuasarInstanceMagnetarAppDataDirectory(instance.InstanceId)
            : instance.MagnetarAppDataPath.Trim();
        instance.WorldPath = string.IsNullOrWhiteSpace(instance.WorldPath)
            ? Path.Combine(instance.DedicatedServerAppDataPath, "Saves", GetDefaultWorldDirectoryName(instance))
            : instance.WorldPath.Trim();
        instance.ConfigFilePath = string.IsNullOrWhiteSpace(instance.ConfigFilePath)
            ? Path.Combine(instance.DedicatedServerAppDataPath, "SpaceEngineers-Dedicated.cfg")
            : instance.ConfigFilePath.Trim();
        instance.ConfigProfileId = instance.ConfigProfileId?.Trim() ?? string.Empty;
        instance.WorldProfileId = instance.WorldProfileId?.Trim() ?? string.Empty;
        instance.LaunchArguments = instance.LaunchArguments?.Trim() ?? string.Empty;
        instance.AutoStart = instance.GoalState == DedicatedServerInstanceGoalState.On || instance.AutoStart;
        instance.GoalState = instance.AutoStart ? DedicatedServerInstanceGoalState.On : DedicatedServerInstanceGoalState.Off;
        if (instance.AgentStartupGraceSeconds < 0)
            instance.AgentStartupGraceSeconds = 0;
        if (instance.AgentHeartbeatTimeoutSeconds < 1)
            instance.AgentHeartbeatTimeoutSeconds = 1;
        if (instance.SimulationProgressWindowSeconds < 1)
            instance.SimulationProgressWindowSeconds = 1;
        if (instance.MinimumSimulationProgressScore < 0f)
            instance.MinimumSimulationProgressScore = 0f;
        else if (instance.MinimumSimulationProgressScore > 1f)
            instance.MinimumSimulationProgressScore = 1f;
        if (instance.WarnAfterUptimeHours < 0)
            instance.WarnAfterUptimeHours = 0;
        if (instance.RecycleAfterUptimeHours < 0)
            instance.RecycleAfterUptimeHours = 0;
        if (instance.RestartDelaySeconds < 0)
            instance.RestartDelaySeconds = 0;
        if (instance.MaxRestartAttempts < 0)
            instance.MaxRestartAttempts = 0;
        if (instance.UpdatedAtUtc == default)
            instance.UpdatedAtUtc = DateTimeOffset.UtcNow;
        return instance;
    }

    private static string GetDefaultWorldDirectoryName(DedicatedServerInstanceDefinition instance)
    {
        var source = string.IsNullOrWhiteSpace(instance.Name)
            ? instance.InstanceId
            : instance.Name;

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = source.Trim();
        foreach (var invalidCharacter in invalidCharacters)
            sanitized = sanitized.Replace(invalidCharacter, '-');

        return string.IsNullOrWhiteSpace(sanitized)
            ? instance.InstanceId
            : sanitized;
    }

    private static DedicatedServerInstanceDefinition Clone(DedicatedServerInstanceDefinition instance)
    {
        return new DedicatedServerInstanceDefinition
        {
            InstanceId = instance.InstanceId,
            Name = instance.Name,
            GoalState = instance.GoalState,
            ExecutablePath = instance.ExecutablePath,
            WorkingDirectory = instance.WorkingDirectory,
            DedicatedServerAppDataPath = instance.DedicatedServerAppDataPath,
            MagnetarAppDataPath = instance.MagnetarAppDataPath,
            WorldPath = instance.WorldPath,
            ConfigFilePath = instance.ConfigFilePath,
            ConfigProfileId = instance.ConfigProfileId,
            WorldProfileId = instance.WorldProfileId,
            LaunchArguments = instance.LaunchArguments,
            AutoStart = instance.AutoStart,
            EnableHealthMonitoring = instance.EnableHealthMonitoring,
            AutoRestartOnUnhealthy = instance.AutoRestartOnUnhealthy,
            AgentStartupGraceSeconds = instance.AgentStartupGraceSeconds,
            AgentHeartbeatTimeoutSeconds = instance.AgentHeartbeatTimeoutSeconds,
            SimulationProgressWindowSeconds = instance.SimulationProgressWindowSeconds,
            MinimumSimulationProgressScore = instance.MinimumSimulationProgressScore,
            WarnAfterUptimeHours = instance.WarnAfterUptimeHours,
            RecycleAfterUptimeHours = instance.RecycleAfterUptimeHours,
            RestartOnCrash = instance.RestartOnCrash,
            RestartDelaySeconds = instance.RestartDelaySeconds,
            MaxRestartAttempts = instance.MaxRestartAttempts,
            UpdatedAtUtc = instance.UpdatedAtUtc,
        };
    }

    private void StartWatching()
    {
        var directory = MagnetarPaths.GetQuasarInstancesDirectory();
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
        if (!IsTrackedInstancePath(args.FullPath))
            return;

        ScheduleReload();
    }

    private static bool IsTrackedInstancePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return string.Equals(Path.GetFileName(path), "instance.json", StringComparison.OrdinalIgnoreCase);
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
        List<DedicatedServerInstanceDefinition> reloaded;
        string snapshot;

        try
        {
            reloaded = LoadInstances();
            snapshot = CreateSnapshot(reloaded);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed reloading Quasar instance catalog from disk.");
            return;
        }

        var changed = false;
        lock (_sync)
        {
            if (!string.Equals(_snapshot, snapshot, StringComparison.Ordinal))
            {
                _instances = reloaded;
                _snapshot = snapshot;
                changed = true;
            }
        }

        if (!changed)
            return;

        _logger.LogInformation("Reloaded Quasar instance catalog from disk after external edit.");
        Changed?.Invoke();
    }

    private static string CreateSnapshot(IEnumerable<DedicatedServerInstanceDefinition> instances)
    {
        var normalized = instances
            .Select(instance => Normalize(Clone(instance)))
            .OrderBy(instance => instance.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(normalized, JsonOptions);
    }
}
