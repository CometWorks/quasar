using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Magnetar.Protocol.Runtime;
using Quasar.Models;

namespace Quasar.Services;

public sealed class DedicatedServerCatalog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private static readonly Regex UniqueNameRegex = new("^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly object _sync = new();
    private readonly ILogger<DedicatedServerCatalog> _logger;
    private List<DedicatedServerDefinition> _servers;
    private string _snapshot;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;

    public DedicatedServerCatalog(ILogger<DedicatedServerCatalog> logger)
    {
        _logger = logger;
        _servers = LoadServers();
        _snapshot = CreateSnapshot(_servers);
        StartWatching();
    }

    public event Action? Changed;

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
    }

    public IReadOnlyList<DedicatedServerDefinition> GetServers()
    {
        lock (_sync)
        {
            return _servers
                .Select(Clone)
                .OrderBy(server => server.UniqueName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public DedicatedServerDefinition? GetServer(string uniqueName)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
            return null;

        lock (_sync)
        {
            return _servers
                .Where(server => string.Equals(server.UniqueName, uniqueName, StringComparison.OrdinalIgnoreCase))
                .Select(Clone)
                .FirstOrDefault();
        }
    }

    public async Task UpsertAsync(DedicatedServerDefinition definition, CancellationToken cancellationToken = default)
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
            if (_servers.Any(server =>
                    string.Equals(server.UniqueName, normalized.UniqueName, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(server.UniqueName, previousUniqueName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"A server named '{normalized.UniqueName}' already exists.");
            }
        }

        PrepareStorageForSave(normalized, previousUniqueName);
        await SaveServerAsync(normalized, cancellationToken);
        ReloadFromDisk();
    }

    public async Task SetGoalStateAsync(
        string uniqueName,
        DedicatedServerGoalState goalState,
        CancellationToken cancellationToken = default)
    {
        var definition = GetServer(uniqueName);
        if (definition is null)
            throw new InvalidOperationException($"Unknown Quasar server '{uniqueName}'.");

        definition.GoalState = goalState;
        definition.AutoStart = goalState == DedicatedServerGoalState.On;
        await UpsertAsync(definition, cancellationToken);
    }

    public async Task DeleteAsync(string uniqueName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
            return;

        if (GetServer(uniqueName) is null)
            return;

        await ArchiveAndDeleteCurrentDefinitionAsync(uniqueName, cancellationToken);
        ReloadFromDisk();
    }

    private List<DedicatedServerDefinition> LoadServers()
    {
        try
        {
            var serversDirectory = MagnetarPaths.GetQuasarServersDirectory();
            if (Directory.Exists(serversDirectory))
            {
                var serverPaths = Directory.GetFiles(serversDirectory, "server.json", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (serverPaths.Count > 0)
                {
                    return serverPaths
                        .Select(LoadServerDefinition)
                        .Where(server => server is not null)
                        .Select(server => Normalize(server!))
                        .ToList();
                }
            }

            return new List<DedicatedServerDefinition>();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load Quasar server catalog.");
            return new List<DedicatedServerDefinition>();
        }
    }

    private DedicatedServerDefinition? LoadServerDefinition(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var definition = JsonSerializer.Deserialize<DedicatedServerDefinition>(json, JsonOptions);
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
                RewriteLegacyServerDefinition(path, definition);

            return definition;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load Quasar server definition from {Path}", path);
            return null;
        }
    }

    private static void RewriteLegacyServerDefinition(string path, DedicatedServerDefinition definition)
    {
        var json = JsonSerializer.Serialize(definition, JsonOptions);
        WriteTextReplacing(path, json);
    }

    private async Task SaveServerAsync(DedicatedServerDefinition definition, CancellationToken cancellationToken)
    {
        definition.UpdatedAtUtc = DateTimeOffset.UtcNow;
        definition.OriginalUniqueName = definition.UniqueName;

        var path = MagnetarPaths.GetQuasarServerDefinitionPath(definition.UniqueName);
        var historyDirectory = MagnetarPaths.GetQuasarServerHistoryDirectory(definition.UniqueName);
        var json = JsonSerializer.Serialize(definition, JsonOptions);

        await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);

        Directory.CreateDirectory(historyDirectory);
        var historyPath = Path.Combine(historyDirectory, $"{definition.UpdatedAtUtc:yyyyMMddHHmmssfff}.json");
        await AtomicFileWriter.WriteTextAsync(historyPath, json, cancellationToken);

        _logger.LogInformation("Saved Quasar server definition to {Path}", path);
    }

    private async Task ArchiveAndDeleteCurrentDefinitionAsync(string uniqueName, CancellationToken cancellationToken)
    {
        var currentPath = MagnetarPaths.GetQuasarServerDefinitionPath(uniqueName);
        if (!File.Exists(currentPath))
            return;

        var historyDirectory = MagnetarPaths.GetQuasarServerHistoryDirectory(uniqueName);
        Directory.CreateDirectory(historyDirectory);

        var deletedContents = await File.ReadAllTextAsync(currentPath, cancellationToken);
        var historyPath = Path.Combine(historyDirectory, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-deleted.json");
        await AtomicFileWriter.WriteTextAsync(historyPath, deletedContents, cancellationToken);

        File.Delete(currentPath);
        _logger.LogInformation("Deleted active Quasar server definition at {Path}", currentPath);
    }

    private static DedicatedServerDefinition Normalize(DedicatedServerDefinition server)
    {
        server.UniqueName = server.UniqueName?.Trim() ?? string.Empty;
        ValidateUniqueName(server.UniqueName);
        server.DisplayName = NormalizeDisplayName(server.DisplayName, server.UniqueName);
        server.OriginalUniqueName = string.IsNullOrWhiteSpace(server.OriginalUniqueName)
            ? server.UniqueName
            : server.OriginalUniqueName.Trim();
        server.ExecutablePath = server.ExecutablePath?.Trim() ?? string.Empty;
        server.WorkingDirectory = server.WorkingDirectory?.Trim() ?? string.Empty;
        server.DedicatedServerAppDataPath = string.IsNullOrWhiteSpace(server.DedicatedServerAppDataPath)
            ? MagnetarPaths.GetQuasarServerDedicatedServerAppDataDirectory(server.UniqueName)
            : server.DedicatedServerAppDataPath.Trim();
        server.MagnetarAppDataPath = string.IsNullOrWhiteSpace(server.MagnetarAppDataPath)
            ? MagnetarPaths.GetQuasarServerMagnetarAppDataDirectory(server.UniqueName)
            : server.MagnetarAppDataPath.Trim();
        server.WorldPath = string.IsNullOrWhiteSpace(server.WorldPath)
            ? Path.Combine(server.DedicatedServerAppDataPath, "Saves", GetDefaultWorldDirectoryName(server))
            : server.WorldPath.Trim();
        server.ConfigFilePath = string.IsNullOrWhiteSpace(server.ConfigFilePath)
            ? Path.Combine(server.DedicatedServerAppDataPath, "SpaceEngineers-Dedicated.cfg")
            : server.ConfigFilePath.Trim();
        server.ConfigProfileId = server.ConfigProfileId?.Trim() ?? string.Empty;
        server.WorldTemplateId = server.WorldTemplateId?.Trim() ?? string.Empty;
        server.LaunchArguments = server.LaunchArguments?.Trim() ?? string.Empty;
        server.AutoStart = server.GoalState == DedicatedServerGoalState.On || server.AutoStart;
        server.GoalState = server.AutoStart ? DedicatedServerGoalState.On : DedicatedServerGoalState.Off;
        if (server.AgentStartupGraceSeconds < 0)
            server.AgentStartupGraceSeconds = 0;
        if (server.AgentHeartbeatTimeoutSeconds < 1)
            server.AgentHeartbeatTimeoutSeconds = 1;
        if (server.SimulationProgressWindowSeconds < 1)
            server.SimulationProgressWindowSeconds = 1;
        if (server.MinimumSimulationProgressScore < 0f)
            server.MinimumSimulationProgressScore = 0f;
        else if (server.MinimumSimulationProgressScore > 1f)
            server.MinimumSimulationProgressScore = 1f;
        if (server.WarnAfterUptimeHours < 0)
            server.WarnAfterUptimeHours = 0;
        if (server.RecycleAfterUptimeHours < 0)
            server.RecycleAfterUptimeHours = 0;
        if (server.RestartDelaySeconds < 0)
            server.RestartDelaySeconds = 0;
        if (server.MaxRestartAttempts < 0)
            server.MaxRestartAttempts = 0;
        server.DailyRestartTimeLocal = server.DailyRestartTimeLocal?.Trim() ?? string.Empty;
        server.MaximumUptime = server.MaximumUptime?.Trim() ?? string.Empty;
        if (!Enum.IsDefined(server.StartupProcessPriority))
            server.StartupProcessPriority = DedicatedServerProcessPriority.BelowNormal;
        if (!Enum.IsDefined(server.ReadyProcessPriority))
            server.ReadyProcessPriority = DedicatedServerProcessPriority.Normal;
        // Re-store the canonical form of a valid affinity; drop anything invalid (or with
        // fewer than the required cores) back to "no affinity" so a bad persisted value can
        // never wedge process startup.
        server.CpuAffinity = CpuAffinitySpec.TryParse(server.CpuAffinity, Environment.ProcessorCount, out var affinityCores, out _)
            ? CpuAffinitySpec.Format(affinityCores)
            : string.Empty;
        if (server.UpdatedAtUtc == default)
            server.UpdatedAtUtc = DateTimeOffset.UtcNow;
        return server;
    }

    private static string GetDefaultWorldDirectoryName(DedicatedServerDefinition server)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = server.UniqueName.Trim();
        foreach (var invalidCharacter in invalidCharacters)
            sanitized = sanitized.Replace(invalidCharacter, '-');

        return string.IsNullOrWhiteSpace(sanitized)
            ? server.UniqueName
            : sanitized;
    }

    private static DedicatedServerDefinition Clone(DedicatedServerDefinition server)
    {
        return new DedicatedServerDefinition
        {
            UniqueName = server.UniqueName,
            DisplayName = server.DisplayName,
            OriginalUniqueName = server.OriginalUniqueName,
            GoalState = server.GoalState,
            ExecutablePath = server.ExecutablePath,
            WorkingDirectory = server.WorkingDirectory,
            DedicatedServerAppDataPath = server.DedicatedServerAppDataPath,
            MagnetarAppDataPath = server.MagnetarAppDataPath,
            WorldPath = server.WorldPath,
            ConfigFilePath = server.ConfigFilePath,
            ConfigProfileId = server.ConfigProfileId,
            WorldTemplateId = server.WorldTemplateId,
            LaunchArguments = server.LaunchArguments,
            ServerPort = server.ServerPort,
            ServerIP = server.ServerIP,
            AutoStart = server.AutoStart,
            EnableHealthMonitoring = server.EnableHealthMonitoring,
            AutoRestartOnUnhealthy = server.AutoRestartOnUnhealthy,
            AgentStartupGraceSeconds = server.AgentStartupGraceSeconds,
            AgentHeartbeatTimeoutSeconds = server.AgentHeartbeatTimeoutSeconds,
            SimulationProgressWindowSeconds = server.SimulationProgressWindowSeconds,
            MinimumSimulationProgressScore = server.MinimumSimulationProgressScore,
            WarnAfterUptimeHours = server.WarnAfterUptimeHours,
            RecycleAfterUptimeHours = server.RecycleAfterUptimeHours,
            RestartOnCrash = server.RestartOnCrash,
            RestartDelaySeconds = server.RestartDelaySeconds,
            MaxRestartAttempts = server.MaxRestartAttempts,
            DailyRestartTimeLocal = server.DailyRestartTimeLocal,
            MaximumUptime = server.MaximumUptime,
            AvoidSimultaneousScheduledRestarts = server.AvoidSimultaneousScheduledRestarts,
            StartupProcessPriority = server.StartupProcessPriority,
            ReadyProcessPriority = server.ReadyProcessPriority,
            CpuAffinity = server.CpuAffinity,
            UpdatedAtUtc = server.UpdatedAtUtc,
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

    private static void PrepareStorageForSave(DedicatedServerDefinition definition, string previousUniqueName)
    {
        if (string.Equals(previousUniqueName, definition.UniqueName, StringComparison.OrdinalIgnoreCase))
            return;

        var previousRoot = MagnetarPaths.GetQuasarServerDirectory(previousUniqueName);
        var currentRoot = MagnetarPaths.GetQuasarServerDirectory(definition.UniqueName);

        definition.DedicatedServerAppDataPath = RewriteManagedPath(definition.DedicatedServerAppDataPath, previousRoot, currentRoot);
        definition.MagnetarAppDataPath = RewriteManagedPath(definition.MagnetarAppDataPath, previousRoot, currentRoot);
        definition.WorldPath = RewriteManagedPath(definition.WorldPath, previousRoot, currentRoot);
        definition.ConfigFilePath = RewriteManagedPath(definition.ConfigFilePath, previousRoot, currentRoot);

        if (!Directory.Exists(previousRoot))
            return;

        if (Directory.Exists(currentRoot))
            throw new InvalidOperationException($"Cannot rename server to '{definition.UniqueName}' because the target directory already exists.");

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
        var directory = MagnetarPaths.GetQuasarServersDirectory();
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
        if (!IsTrackedServerPath(args.FullPath))
            return;

        ScheduleReload();
    }

    private static bool IsTrackedServerPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return string.Equals(Path.GetFileName(path), "server.json", StringComparison.OrdinalIgnoreCase);
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
        List<DedicatedServerDefinition> reloaded;
        string snapshot;

        try
        {
            reloaded = LoadServers();
            snapshot = CreateSnapshot(reloaded);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed reloading Quasar server catalog from disk.");
            return;
        }

        var changed = false;
        lock (_sync)
        {
            if (!string.Equals(_snapshot, snapshot, StringComparison.Ordinal))
            {
                _servers = reloaded;
                _snapshot = snapshot;
                changed = true;
            }
        }

        if (!changed)
            return;

        _logger.LogInformation("Reloaded Quasar server catalog from disk after external edit.");
        Changed?.Invoke();
    }

    private static string CreateSnapshot(IEnumerable<DedicatedServerDefinition> servers)
    {
        var normalized = servers
            .Select(server => Normalize(Clone(server)))
            .OrderBy(server => server.UniqueName, StringComparer.OrdinalIgnoreCase)
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
