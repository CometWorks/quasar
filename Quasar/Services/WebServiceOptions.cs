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

    public bool Headless { get; init; }

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

        var headlessValue = Environment.GetEnvironmentVariable("QUASAR_HEADLESS")
                            ?? section["Headless"]
                            ?? "false";
        if (!bool.TryParse(headlessValue, out var headless))
            headless = false;

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
            Headless = headless,
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

    // Same consent schema as the diagnostics uplink. Legacy YES does not grant diagnostics.
    public int PolicyVersion { get; set; }
    public bool DiagnosticsGranted { get; set; }
    public bool DumpsGranted { get; set; }
    public long Generation { get; set; }
    public DateTimeOffset GrantedSinceUtc { get; set; }

    public DataHandlingConsentSettings Clone() =>
        new()
        {
            ConsentGranted = ConsentGranted,
            DecisionDateUtc = DecisionDateUtc,
            PolicyVersion = PolicyVersion,
            DiagnosticsGranted = DiagnosticsGranted,
            DumpsGranted = DumpsGranted,
            Generation = Generation,
            GrantedSinceUtc = GrantedSinceUtc,
        };

    public static DataHandlingConsentSettings Normalize(DataHandlingConsentSettings? settings)
    {
        settings ??= new DataHandlingConsentSettings();

        return new DataHandlingConsentSettings
        {
            ConsentGranted = settings.ConsentGranted,
            PolicyVersion = settings.PolicyVersion,
            DiagnosticsGranted = settings.PolicyVersion == 2 && settings.Generation > 0
                && settings.GrantedSinceUtc != default && settings.DiagnosticsGranted,
            DumpsGranted = settings.PolicyVersion == 2 && settings.Generation > 0
                && settings.GrantedSinceUtc != default && settings.DiagnosticsGranted && settings.DumpsGranted,
            Generation = Math.Max(0, settings.Generation),
            GrantedSinceUtc = settings.GrantedSinceUtc,
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
    private DebouncedFileWatcher? _watcher;

    public DataHandlingConsentCatalog(ILogger<DataHandlingConsentCatalog> logger)
        : this(logger, MagnetarPaths.GetQuasarDataHandlingConsentPath()) { }

    internal DataHandlingConsentCatalog(ILogger<DataHandlingConsentCatalog> logger, string path)
    {
        _logger = logger;
        SettingsPath = path;
        _settings = LoadSettings();
        _snapshot = CreateSnapshot(_settings);
        StartWatching();
    }

    public event Action? Changed;

    public string SettingsPath { get; }

    public void Dispose()
    {
        _watcher?.Dispose();
        // The watcher can still be completing a reload while shutdown begins.
    }

    public DataHandlingConsentSettings GetSettings()
    {
        lock (_sync)
        {
            return _settings.Clone();
        }
    }

    public Task SaveAsync(bool consentGranted, CancellationToken cancellationToken = default) =>
        SaveCoreAsync(consentGranted, null, null, cancellationToken);

    public Task SaveAsync(bool? consentGranted, bool diagnosticsGranted, bool dumpsGranted,
        CancellationToken cancellationToken = default) =>
        SaveCoreAsync(consentGranted, diagnosticsGranted, dumpsGranted, cancellationToken);

    private async Task SaveCoreAsync(bool? consentGranted, bool? diagnosticsGranted, bool? dumpsGranted,
        CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var previous = GetSettings();
            var next = previous.Clone();
            next.ConsentGranted = consentGranted;
            next.DecisionDateUtc = DateTimeOffset.UtcNow.ToString("O");
            if (diagnosticsGranted.HasValue)
            {
                next.PolicyVersion = 2;
                next.DiagnosticsGranted = diagnosticsGranted.Value;
                next.DumpsGranted = diagnosticsGranted.Value && dumpsGranted == true;
            }
            AdvanceGeneration(next, previous);
            var normalized = DataHandlingConsentSettings.Normalize(next);
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

    private static void AdvanceGeneration(DataHandlingConsentSettings next, DataHandlingConsentSettings previous)
    {
        if (next.DiagnosticsGranted != previous.DiagnosticsGranted || next.DumpsGranted != previous.DumpsGranted
            || next.Generation != previous.Generation || next.GrantedSinceUtc != previous.GrantedSinceUtc)
        {
            next.Generation = checked(Math.Max(next.Generation, previous.Generation) + 1);
            next.GrantedSinceUtc = DateTimeOffset.UtcNow;
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
        _watcher = DebouncedFileWatcher.WatchFile(SettingsPath, ReloadFromDisk);
    }

    internal void ReloadFromDisk()
    {
        _saveGate.Wait();
        try
        {
            var reloaded = LoadSettings();
            if (CreateSnapshot(reloaded) == _snapshot) return;
            AdvanceGeneration(reloaded, GetSettings());
            var snapshot = CreateSnapshot(reloaded);
            // External category changes must also fence old diagnostic archives after restart.
            AtomicFileWriter.WriteTextAsync(SettingsPath, snapshot, CancellationToken.None).GetAwaiter().GetResult();
            lock (_sync)
            {
                _settings = reloaded;
                _snapshot = snapshot;
            }
            _logger.LogInformation("Reloaded data handling consent settings from disk after external edit.");
            Changed?.Invoke();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed reloading data handling consent; publication disabled.");
            var denied = GetSettings();
            denied.ConsentGranted = null;
            denied.DiagnosticsGranted = denied.DumpsGranted = false;
            AdvanceGeneration(denied, GetSettings());
            lock (_sync) { _settings = denied; _snapshot = CreateSnapshot(denied); }
            Changed?.Invoke();
        }
        finally { _saveGate.Release(); }
    }

    private static string CreateSnapshot(DataHandlingConsentSettings settings) =>
        JsonSerializer.Serialize(DataHandlingConsentSettings.Normalize(settings), JsonOptions);
}
