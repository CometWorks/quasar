using Magnetar.Protocol.Runtime;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quasar.Services;

public sealed class WebServiceOptions
{
    public const string SupervisorName = "Quasar";

    public string Host { get; init; } = "0.0.0.0";

    public int Port { get; init; } = 8080;

    public string WorkerId { get; init; } = Guid.NewGuid().ToString("N");

    public string HostId { get; init; } = Environment.MachineName.ToLowerInvariant();

    public string HostName { get; init; } = Environment.MachineName;

    public string BaseUrl { get; init; } = "http://127.0.0.1:8080";

    public string ListenUrl { get; init; } = "http://0.0.0.0:8080";

    public string Version { get; init; } = QuasarReleaseVersion.GetEntryAssemblyVersion();

    public string BootstrapVersion { get; init; } = string.Empty;

    public string Mode { get; init; } = "Console";

    public bool OpenBrowserOnStart { get; init; } = true;

    public string BackupDirectory { get; set; } = MagnetarPaths.GetQuasarBackupsDirectory();

    public string LoggingDirectory { get; init; } = MagnetarPaths.GetQuasarLogDirectory();

    public string LoggingFormat { get; init; } = "text";

    public string LoggingMinimumLevel { get; init; } = "Info";

    public bool IsDevelopment { get; init; }

    public bool DisableServerHealthMonitoring { get; init; }

    public bool OwnManifest { get; init; } = true;

    public bool PreserveManagedServersOnShutdown { get; init; } = true;

    public bool AvoidSimultaneousScheduledRestarts { get; init; } = true;

    // Passed to each launched Quasar.Agent so it knows how to behave when it
    // loses contact with Quasar. See AgentOptions in the Quasar.Agent project.
    public int AgentOfflineShutdownSeconds { get; init; } = 3600;

    public int AgentReconnectIntervalSeconds { get; init; } = 10;

    public int AgentReconnectJitterSeconds { get; init; } = 3;

    public string AgentProfilerMode { get; init; } = "SafeContinuous";

    public string LauncherToken { get; init; } = string.Empty;

    public bool IsServiceMode => string.Equals(Mode, "service", StringComparison.OrdinalIgnoreCase);

    public static WebServiceOptions Create(IConfiguration configuration)
    {
        var section = configuration.GetSection("Quasar");
        if (!section.Exists())
            section = configuration.GetSection("MagnetarWeb");

        var loggingSection = section.GetSection("Logging");
        var host = Environment.GetEnvironmentVariable("QUASAR_WEB_HOST")
                   ?? Environment.GetEnvironmentVariable("MAGNETAR_WEB_HOST")
                   ?? section["Host"]
                   ?? "0.0.0.0";

        var portValue = Environment.GetEnvironmentVariable("QUASAR_WEB_PORT")
                        ?? Environment.GetEnvironmentVariable("MAGNETAR_WEB_PORT")
                        ?? section["Port"]
                        ?? "8080";

        if (!int.TryParse(portValue, out var port) || port <= 0)
            port = 8080;

        var hostName = Environment.MachineName;
        var hostId = Environment.GetEnvironmentVariable("QUASAR_HOST_ID")
                     ?? Environment.GetEnvironmentVariable("MAGNETAR_HOST_ID");
        if (string.IsNullOrWhiteSpace(hostId))
            hostId = hostName.ToLowerInvariant();

        var mode = Environment.GetEnvironmentVariable("QUASAR_MODE")
                   ?? section["Mode"]
                   ?? "Console";

        var openBrowserValue = Environment.GetEnvironmentVariable("QUASAR_OPEN_BROWSER_ON_START")
                               ?? section["OpenBrowserOnStart"]
                               ?? "true";

        if (!bool.TryParse(openBrowserValue, out var openBrowserOnStart))
            openBrowserOnStart = true;

        var backupDirectory = ResolveBackupDirectory(
            Environment.GetEnvironmentVariable("QUASAR_BACKUP_DIR") ?? section["BackupDirectory"]);

        var loggingDirectory = Environment.GetEnvironmentVariable("QUASAR_LOG_DIR")
                               ?? loggingSection["Directory"];
        if (string.IsNullOrWhiteSpace(loggingDirectory))
            loggingDirectory = MagnetarPaths.GetQuasarLogDirectory();

        var loggingFormat = Environment.GetEnvironmentVariable("QUASAR_LOG_FORMAT")
                            ?? loggingSection["Format"];
        if (string.IsNullOrWhiteSpace(loggingFormat))
            loggingFormat = "text";

        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                              ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                              ?? "Production";
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);

        var loggingMinimumLevel = Environment.GetEnvironmentVariable("QUASAR_LOG_MIN_LEVEL")
                                  ?? loggingSection["MinimumLevel"];
        // Deployments stay quiet at Warn by default; development keeps the more verbose Info.
        if (string.IsNullOrWhiteSpace(loggingMinimumLevel))
            loggingMinimumLevel = isDevelopment ? "Info" : "Warn";

        var disableServerHealthMonitoringValue = Environment.GetEnvironmentVariable("QUASAR_DISABLE_SERVER_HEALTH_MONITORING")
                                                  ?? section["DisableServerHealthMonitoring"];
        if (!bool.TryParse(disableServerHealthMonitoringValue, out var disableServerHealthMonitoring))
            disableServerHealthMonitoring = isDevelopment;

        var advertisedHost = host switch
        {
            "0.0.0.0" => "127.0.0.1",
            "*" => "127.0.0.1",
            "+" => "127.0.0.1",
            _ => host,
        };

        var baseUrl = Environment.GetEnvironmentVariable("QUASAR_PUBLIC_BASE_URL")
                      ?? Environment.GetEnvironmentVariable("MAGNETAR_WEB_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = $"http://{advertisedHost}:{port}";

        var ownManifestValue = Environment.GetEnvironmentVariable("QUASAR_OWN_MANIFEST") ?? "true";
        if (!bool.TryParse(ownManifestValue, out var ownManifest))
            ownManifest = true;

        var preserveServersValue = Environment.GetEnvironmentVariable("QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN")
                                     ?? section["PreserveManagedServersOnShutdown"]
                                     ?? "true";
        if (!bool.TryParse(preserveServersValue, out var preserveManagedServersOnShutdown))
            preserveManagedServersOnShutdown = true;

        var agentOfflineShutdownValue = Environment.GetEnvironmentVariable("QUASAR_AGENT_OFFLINE_SHUTDOWN_SECONDS")
                                        ?? section["AgentOfflineShutdownSeconds"];
        // Zero/negative is meaningful (agent stops promptly when Quasar is gone),
        // so only fall back to the default when the value is missing or unparsable.
        if (!int.TryParse(agentOfflineShutdownValue, out var agentOfflineShutdownSeconds))
            agentOfflineShutdownSeconds = 3600;

        var agentReconnectIntervalValue = Environment.GetEnvironmentVariable("QUASAR_AGENT_RECONNECT_INTERVAL_SECONDS")
                                          ?? section["AgentReconnectIntervalSeconds"];
        if (!int.TryParse(agentReconnectIntervalValue, out var agentReconnectIntervalSeconds) || agentReconnectIntervalSeconds < 1)
            agentReconnectIntervalSeconds = 10;

        var agentReconnectJitterValue = Environment.GetEnvironmentVariable("QUASAR_AGENT_RECONNECT_JITTER_SECONDS")
                                        ?? section["AgentReconnectJitterSeconds"];
        if (!int.TryParse(agentReconnectJitterValue, out var agentReconnectJitterSeconds) || agentReconnectJitterSeconds < 0)
            agentReconnectJitterSeconds = 3;

        var agentProfilerMode = Environment.GetEnvironmentVariable("QUASAR_AGENT_PROFILER_MODE")
                                ?? section["AgentProfilerMode"]
                                ?? "SafeContinuous";
        if (string.IsNullOrWhiteSpace(agentProfilerMode))
            agentProfilerMode = "SafeContinuous";
        agentProfilerMode = DedicatedServerCatalog.NormalizeProfilerMode(agentProfilerMode);

        var avoidSimultaneousScheduledRestartsValue =
            Environment.GetEnvironmentVariable("QUASAR_AVOID_SIMULTANEOUS_SCHEDULED_RESTARTS")
            ?? section["AvoidSimultaneousScheduledRestarts"];
        if (!bool.TryParse(avoidSimultaneousScheduledRestartsValue, out var avoidSimultaneousScheduledRestarts))
            avoidSimultaneousScheduledRestarts = true;

        var launcherToken = Environment.GetEnvironmentVariable("QUASAR_LAUNCHER_TOKEN") ?? string.Empty;
        var bootstrapVersion = Environment.GetEnvironmentVariable("QUASAR_BOOTSTRAP_VERSION") ?? string.Empty;

        return new WebServiceOptions
        {
            Host = host,
            Port = port,
            HostId = hostId,
            HostName = hostName,
            Mode = mode,
            OpenBrowserOnStart = openBrowserOnStart,
            BackupDirectory = backupDirectory,
            LoggingDirectory = loggingDirectory,
            LoggingFormat = loggingFormat,
            LoggingMinimumLevel = loggingMinimumLevel,
            IsDevelopment = isDevelopment,
            DisableServerHealthMonitoring = disableServerHealthMonitoring,
            BaseUrl = baseUrl,
            ListenUrl = $"http://{host}:{port}",
            OwnManifest = ownManifest,
            PreserveManagedServersOnShutdown = preserveManagedServersOnShutdown,
            AvoidSimultaneousScheduledRestarts = avoidSimultaneousScheduledRestarts,
            AgentOfflineShutdownSeconds = agentOfflineShutdownSeconds,
            AgentReconnectIntervalSeconds = agentReconnectIntervalSeconds,
            AgentReconnectJitterSeconds = agentReconnectJitterSeconds,
            AgentProfilerMode = agentProfilerMode,
            LauncherToken = launcherToken,
            BootstrapVersion = bootstrapVersion,
        };
    }

    public static string ResolveBackupDirectory(string? value) =>
        ResolveDirectoryOption(value, MagnetarPaths.GetQuasarBackupsDirectory());

    private static string ResolveDirectoryOption(string? value, string defaultPath)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Path.GetFullPath(defaultPath);

        var directory = value.Trim();
        if (!Path.IsPathRooted(directory))
            directory = Path.Combine(MagnetarPaths.GetQuasarDirectory(), directory);

        return Path.GetFullPath(directory);
    }
}

public sealed class DataHandlingConsentSettings
{
    public bool? ConsentGranted { get; set; }

    public string? DecisionDateUtc { get; set; }

    public DataHandlingConsentSettings Clone() =>
        new()
        {
            ConsentGranted = ConsentGranted,
            DecisionDateUtc = DecisionDateUtc,
        };

    public static DataHandlingConsentSettings Normalize(DataHandlingConsentSettings? settings)
    {
        settings ??= new DataHandlingConsentSettings();

        return new DataHandlingConsentSettings
        {
            ConsentGranted = settings.ConsentGranted,
            DecisionDateUtc = string.IsNullOrWhiteSpace(settings.DecisionDateUtc)
                ? null
                : settings.DecisionDateUtc.Trim(),
        };
    }
}

public sealed class DataHandlingConsentCatalog : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly ILogger<DataHandlingConsentCatalog> _logger;
    private DataHandlingConsentSettings _settings;
    private string _snapshot;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadDebounce;

    public DataHandlingConsentCatalog(ILogger<DataHandlingConsentCatalog> logger)
    {
        _logger = logger;
        _settings = LoadSettings();
        _snapshot = CreateSnapshot(_settings);
        StartWatching();
    }

    public event Action? Changed;

    public string SettingsPath => MagnetarPaths.GetQuasarDataHandlingConsentPath();

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
        _saveGate.Dispose();
    }

    public DataHandlingConsentSettings GetSettings()
    {
        lock (_sync)
        {
            return _settings.Clone();
        }
    }

    public async Task SaveAsync(bool consentGranted, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var normalized = DataHandlingConsentSettings.Normalize(new DataHandlingConsentSettings
            {
                ConsentGranted = consentGranted,
                DecisionDateUtc = DateTimeOffset.UtcNow.ToString("O"),
            });
            var json = JsonSerializer.Serialize(normalized, JsonOptions);
            var path = SettingsPath;

            await AtomicFileWriter.WriteTextAsync(path, json, cancellationToken);

            lock (_sync)
            {
                _settings = normalized.Clone();
                _snapshot = json;
            }

            _logger.LogInformation(
                "Saved data handling consent decision {ConsentGranted} to {Path}.",
                consentGranted,
                path);
            Changed?.Invoke();
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private DataHandlingConsentSettings LoadSettings()
    {
        var path = SettingsPath;

        try
        {
            if (!File.Exists(path))
                return DataHandlingConsentSettings.Normalize(null);

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<DataHandlingConsentSettings>(json, JsonOptions);
            return DataHandlingConsentSettings.Normalize(settings);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed loading data handling consent settings from {Path}", path);
            return DataHandlingConsentSettings.Normalize(null);
        }
    }

    private void StartWatching()
    {
        var path = SettingsPath;
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
            Path.GetFullPath(SettingsPath),
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
        DataHandlingConsentSettings reloaded;
        string snapshot;

        try
        {
            reloaded = LoadSettings();
            snapshot = CreateSnapshot(reloaded);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed reloading data handling consent settings from disk.");
            return;
        }

        var changed = false;
        lock (_sync)
        {
            if (!string.Equals(_snapshot, snapshot, StringComparison.Ordinal))
            {
                _settings = reloaded;
                _snapshot = snapshot;
                changed = true;
            }
        }

        if (!changed)
            return;

        _logger.LogInformation("Reloaded data handling consent settings from disk after external edit.");
        Changed?.Invoke();
    }

    private static string CreateSnapshot(DataHandlingConsentSettings settings) =>
        JsonSerializer.Serialize(DataHandlingConsentSettings.Normalize(settings), JsonOptions);
}
