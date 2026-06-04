using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

    private static readonly Regex UniqueNameRegex = new("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

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
                .OrderBy(instance => instance.UniqueName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public DedicatedServerInstanceDefinition? GetInstance(string uniqueName)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
            return null;

        lock (_sync)
        {
            return _instances
                .Where(instance => string.Equals(instance.UniqueName, uniqueName, StringComparison.OrdinalIgnoreCase))
                .Select(Clone)
                .FirstOrDefault();
        }
    }

    public async Task UpsertAsync(DedicatedServerInstanceDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var normalized = Normalize(Clone(definition));
        if (string.IsNullOrWhiteSpace(normalized.WorldTemplateId))
            throw new InvalidOperationException("World template required.");

        var previousUniqueName = string.IsNullOrWhiteSpace(normalized.OriginalUniqueName)
            ? normalized.UniqueName
            : normalized.OriginalUniqueName;

        lock (_sync)
        {
            if (_instances.Any(instance =>
                    string.Equals(instance.UniqueName, normalized.UniqueName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(instance.UniqueName, previousUniqueName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"An instance named '{normalized.UniqueName}' already exists.");
            }
        }

        PrepareStorageForSave(normalized, previousUniqueName);
        await SaveInstanceAsync(normalized, cancellationToken);
        ReloadFromDisk();
    }

    public async Task SetGoalStateAsync(
        string uniqueName,
        DedicatedServerInstanceGoalState goalState,
        CancellationToken cancellationToken = default)
    {
        var definition = GetInstance(uniqueName);
        if (definition is null)
            throw new InvalidOperationException($"Unknown Quasar instance '{uniqueName}'.");

        definition.GoalState = goalState;
        definition.AutoStart = goalState == DedicatedServerInstanceGoalState.On;
        await UpsertAsync(definition, cancellationToken);
    }

    public async Task DeleteAsync(string uniqueName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
            return;

        if (GetInstance(uniqueName) is null)
            return;

        await ArchiveAndDeleteCurrentDefinitionAsync(uniqueName, cancellationToken);
        ReloadFromDisk();
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

            return new List<DedicatedServerInstanceDefinition>();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load Quasar instance catalog.");
            return new List<DedicatedServerInstanceDefinition>();
        }
    }

    private DedicatedServerInstanceDefinition? LoadInstanceDefinition(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var definition = JsonSerializer.Deserialize<DedicatedServerInstanceDefinition>(json, JsonOptions);
            if (definition is null)
                return null;

            using var document = JsonDocument.Parse(json);
            var hasLegacyWorldTemplateId = document.RootElement.TryGetProperty("worldProfileId", out var legacyWorldProfileId);
            if (string.IsNullOrWhiteSpace(definition.WorldTemplateId) &&
                hasLegacyWorldTemplateId &&
                legacyWorldProfileId.ValueKind == JsonValueKind.String)
            {
                definition.WorldTemplateId = legacyWorldProfileId.GetString() ?? string.Empty;
            }

            if (hasLegacyWorldTemplateId)
                RewriteLegacyInstanceDefinition(path, definition);

            return definition;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load Quasar instance definition from {Path}", path);
            return null;
        }
    }

    private static void RewriteLegacyInstanceDefinition(string path, DedicatedServerInstanceDefinition definition)
    {
        var json = JsonSerializer.Serialize(definition, JsonOptions);
        WriteTextReplacing(path, json);
    }

    private async Task SaveInstanceAsync(DedicatedServerInstanceDefinition definition, CancellationToken cancellationToken)
    {
        definition.UpdatedAtUtc = DateTimeOffset.UtcNow;
        definition.OriginalUniqueName = definition.UniqueName;

        var path = MagnetarPaths.GetQuasarInstanceDefinitionPath(definition.UniqueName);
        var historyDirectory = MagnetarPaths.GetQuasarInstanceHistoryDirectory(definition.UniqueName);
        var json = JsonSerializer.Serialize(definition, JsonOptions);

        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);

        Directory.CreateDirectory(historyDirectory);
        var historyPath = Path.Combine(historyDirectory, $"{definition.UpdatedAtUtc:yyyyMMddHHmmssfff}.json");
        await AtomicFileWriter.WriteTextAsync(historyPath, json, cancellationToken);

        _logger.LogInformation("Saved Quasar instance definition to {Path}", path);
    }

    private async Task ArchiveAndDeleteCurrentDefinitionAsync(string uniqueName, CancellationToken cancellationToken)
    {
        var currentPath = MagnetarPaths.GetQuasarInstanceDefinitionPath(uniqueName);
        if (!File.Exists(currentPath))
            return;

        var historyDirectory = MagnetarPaths.GetQuasarInstanceHistoryDirectory(uniqueName);
        Directory.CreateDirectory(historyDirectory);

        var deletedContents = await File.ReadAllTextAsync(currentPath, cancellationToken);
        var historyPath = Path.Combine(historyDirectory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-deleted.json");
        await AtomicFileWriter.WriteTextAsync(historyPath, deletedContents, cancellationToken);

        File.Delete(currentPath);
        _logger.LogInformation("Deleted active Quasar instance definition at {Path}", currentPath);
    }

    private static DedicatedServerInstanceDefinition Normalize(DedicatedServerInstanceDefinition instance)
    {
        instance.UniqueName = instance.UniqueName?.Trim() ?? string.Empty;
        ValidateUniqueName(instance.UniqueName);
        instance.DisplayName = NormalizeDisplayName(instance.DisplayName, instance.UniqueName);
        instance.OriginalUniqueName = string.IsNullOrWhiteSpace(instance.OriginalUniqueName)
            ? instance.UniqueName
            : instance.OriginalUniqueName.Trim();
        instance.ExecutablePath = instance.ExecutablePath?.Trim() ?? string.Empty;
        instance.WorkingDirectory = instance.WorkingDirectory?.Trim() ?? string.Empty;
        instance.DedicatedServerAppDataPath = string.IsNullOrWhiteSpace(instance.DedicatedServerAppDataPath)
            ? MagnetarPaths.GetQuasarInstanceDedicatedServerAppDataDirectory(instance.UniqueName)
            : instance.DedicatedServerAppDataPath.Trim();
        instance.MagnetarAppDataPath = string.IsNullOrWhiteSpace(instance.MagnetarAppDataPath)
            ? MagnetarPaths.GetQuasarInstanceMagnetarAppDataDirectory(instance.UniqueName)
            : instance.MagnetarAppDataPath.Trim();
        instance.WorldPath = string.IsNullOrWhiteSpace(instance.WorldPath)
            ? Path.Combine(instance.DedicatedServerAppDataPath, "Saves", GetDefaultWorldDirectoryName(instance))
            : instance.WorldPath.Trim();
        instance.ConfigFilePath = string.IsNullOrWhiteSpace(instance.ConfigFilePath)
            ? Path.Combine(instance.DedicatedServerAppDataPath, "SpaceEngineers-Dedicated.cfg")
            : instance.ConfigFilePath.Trim();
        instance.ConfigProfileId = instance.ConfigProfileId?.Trim() ?? string.Empty;
        instance.WorldTemplateId = instance.WorldTemplateId?.Trim() ?? string.Empty;
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
        instance.DailyRestartTimeLocal = instance.DailyRestartTimeLocal?.Trim() ?? string.Empty;
        instance.MaximumUptime = instance.MaximumUptime?.Trim() ?? string.Empty;
        if (!Enum.IsDefined(instance.StartupProcessPriority))
            instance.StartupProcessPriority = DedicatedServerProcessPriority.BelowNormal;
        if (!Enum.IsDefined(instance.ReadyProcessPriority))
            instance.ReadyProcessPriority = DedicatedServerProcessPriority.Normal;
        if (instance.UpdatedAtUtc == default)
            instance.UpdatedAtUtc = DateTimeOffset.UtcNow;
        return instance;
    }

    private static string GetDefaultWorldDirectoryName(DedicatedServerInstanceDefinition instance)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = instance.UniqueName.Trim();
        foreach (var invalidCharacter in invalidCharacters)
            sanitized = sanitized.Replace(invalidCharacter, '-');

        return string.IsNullOrWhiteSpace(sanitized)
            ? instance.UniqueName
            : sanitized;
    }

    private static DedicatedServerInstanceDefinition Clone(DedicatedServerInstanceDefinition instance)
    {
        return new DedicatedServerInstanceDefinition
        {
            UniqueName = instance.UniqueName,
            DisplayName = instance.DisplayName,
            OriginalUniqueName = instance.OriginalUniqueName,
            GoalState = instance.GoalState,
            ExecutablePath = instance.ExecutablePath,
            WorkingDirectory = instance.WorkingDirectory,
            DedicatedServerAppDataPath = instance.DedicatedServerAppDataPath,
            MagnetarAppDataPath = instance.MagnetarAppDataPath,
            WorldPath = instance.WorldPath,
            ConfigFilePath = instance.ConfigFilePath,
            ConfigProfileId = instance.ConfigProfileId,
            WorldTemplateId = instance.WorldTemplateId,
            LaunchArguments = instance.LaunchArguments,
            ServerPort = instance.ServerPort,
            ServerIP = instance.ServerIP,
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
            DailyRestartTimeLocal = instance.DailyRestartTimeLocal,
            MaximumUptime = instance.MaximumUptime,
            AvoidSimultaneousScheduledRestarts = instance.AvoidSimultaneousScheduledRestarts,
            StartupProcessPriority = instance.StartupProcessPriority,
            ReadyProcessPriority = instance.ReadyProcessPriority,
            UpdatedAtUtc = instance.UpdatedAtUtc,
        };
    }

    private static string NormalizeDisplayName(string? displayName, string uniqueName)
    {
        var normalized = WhitespaceRegex.Replace(displayName?.Trim() ?? string.Empty, " ");
        return string.IsNullOrWhiteSpace(normalized) ? uniqueName : normalized;
    }

    private static void ValidateUniqueName(string uniqueName)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
            throw new InvalidOperationException("Unique name required.");

        var trimmed = uniqueName.Trim();
        if (!UniqueNameRegex.IsMatch(trimmed))
            throw new InvalidOperationException("Unique name must match ^[a-zA-Z0-9_-]+$.");
    }

    private static void PrepareStorageForSave(DedicatedServerInstanceDefinition definition, string previousUniqueName)
    {
        if (string.Equals(previousUniqueName, definition.UniqueName, StringComparison.OrdinalIgnoreCase))
            return;

        var previousRoot = MagnetarPaths.GetQuasarInstanceDirectory(previousUniqueName);
        var currentRoot = MagnetarPaths.GetQuasarInstanceDirectory(definition.UniqueName);

        definition.DedicatedServerAppDataPath = RewriteManagedPath(definition.DedicatedServerAppDataPath, previousRoot, currentRoot);
        definition.MagnetarAppDataPath = RewriteManagedPath(definition.MagnetarAppDataPath, previousRoot, currentRoot);
        definition.WorldPath = RewriteManagedPath(definition.WorldPath, previousRoot, currentRoot);
        definition.ConfigFilePath = RewriteManagedPath(definition.ConfigFilePath, previousRoot, currentRoot);

        if (!Directory.Exists(previousRoot))
            return;

        if (Directory.Exists(currentRoot))
            throw new InvalidOperationException($"Cannot rename instance to '{definition.UniqueName}' because the target directory already exists.");

        Directory.CreateDirectory(Path.GetDirectoryName(currentRoot)!);
        Directory.Move(previousRoot, currentRoot);
    }

    private static string RewriteManagedPath(string path, string previousRoot, string currentRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        var fullPath = Path.GetFullPath(path);
        var fullPreviousRoot = Path.GetFullPath(previousRoot);
        if (!IsPathWithinRoot(fullPath, fullPreviousRoot))
            return path;

        var relative = Path.GetRelativePath(fullPreviousRoot, fullPath);
        return Path.Combine(currentRoot, relative);
    }

    private static bool IsPathWithinRoot(string fullPath, string fullRoot)
    {
        if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
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
            .OrderBy(instance => instance.UniqueName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static void WriteTextReplacing(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, content);
        File.Move(tempPath, path, overwrite: true);
    }
}
