using Magnetar.Protocol.Runtime;
using System.Reflection;

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

        var loggingDirectory = Environment.GetEnvironmentVariable("QUASAR_LOG_DIR")
                               ?? loggingSection["Directory"];
        if (string.IsNullOrWhiteSpace(loggingDirectory))
            loggingDirectory = MagnetarPaths.GetQuasarLogDirectory();

        var loggingFormat = Environment.GetEnvironmentVariable("QUASAR_LOG_FORMAT")
                            ?? loggingSection["Format"];
        if (string.IsNullOrWhiteSpace(loggingFormat))
            loggingFormat = "text";

        var loggingMinimumLevel = Environment.GetEnvironmentVariable("QUASAR_LOG_MIN_LEVEL")
                                  ?? loggingSection["MinimumLevel"];
        if (string.IsNullOrWhiteSpace(loggingMinimumLevel))
            loggingMinimumLevel = "Info";

        var environmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                              ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                              ?? "Production";
        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);

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
            LauncherToken = launcherToken,
            BootstrapVersion = bootstrapVersion,
        };
    }
}
