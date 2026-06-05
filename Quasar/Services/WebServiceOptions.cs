using Magnetar.Protocol.Runtime;
using System.Reflection;

namespace Quasar.Services;

public sealed class WebServiceOptions
{
    public const string SupervisorName = "Quasar";

    public string Host { get; init; } = "0.0.0.0";

    public int Port { get; init; } = 58631;

    public string InstanceId { get; init; } = Guid.NewGuid().ToString("N");

    public string NodeId { get; init; } = Environment.MachineName.ToLowerInvariant();

    public string NodeName { get; init; } = Environment.MachineName;

    public string BaseUrl { get; init; } = "http://127.0.0.1:58631";

    public string ListenUrl { get; init; } = "http://0.0.0.0:58631";

    public string Version { get; init; } = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";

    public string Mode { get; init; } = "Console";

    public bool OpenBrowserOnStart { get; init; } = true;

    public string LoggingDirectory { get; init; } = MagnetarPaths.GetQuasarLogDirectory();

    public string LoggingFormat { get; init; } = "text";

    public string LoggingMinimumLevel { get; init; } = "Info";

    public bool IsDevelopment { get; init; }

    public bool DisableInstanceHealthMonitoring { get; init; }

    public bool OwnManifest { get; init; } = true;

    public bool PreserveManagedInstancesOnShutdown { get; init; } = true;

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
                        ?? "58631";

        if (!int.TryParse(portValue, out var port) || port <= 0)
            port = 58631;

        var nodeName = Environment.MachineName;
        var nodeId = Environment.GetEnvironmentVariable("MAGNETAR_NODE_ID");
        if (string.IsNullOrWhiteSpace(nodeId))
            nodeId = nodeName.ToLowerInvariant();

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

        var disableInstanceHealthMonitoringValue = Environment.GetEnvironmentVariable("QUASAR_DISABLE_INSTANCE_HEALTH_MONITORING")
                                                  ?? section["DisableInstanceHealthMonitoring"];
        if (!bool.TryParse(disableInstanceHealthMonitoringValue, out var disableInstanceHealthMonitoring))
            disableInstanceHealthMonitoring = isDevelopment;

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

        var preserveInstancesValue = Environment.GetEnvironmentVariable("QUASAR_PRESERVE_INSTANCES_ON_SHUTDOWN")
                                     ?? section["PreserveManagedInstancesOnShutdown"]
                                     ?? "true";
        if (!bool.TryParse(preserveInstancesValue, out var preserveManagedInstancesOnShutdown))
            preserveManagedInstancesOnShutdown = true;

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

        return new WebServiceOptions
        {
            Host = host,
            Port = port,
            NodeId = nodeId,
            NodeName = nodeName,
            Mode = mode,
            OpenBrowserOnStart = openBrowserOnStart,
            LoggingDirectory = loggingDirectory,
            LoggingFormat = loggingFormat,
            LoggingMinimumLevel = loggingMinimumLevel,
            IsDevelopment = isDevelopment,
            DisableInstanceHealthMonitoring = disableInstanceHealthMonitoring,
            BaseUrl = baseUrl,
            ListenUrl = $"http://{host}:{port}",
            OwnManifest = ownManifest,
            PreserveManagedInstancesOnShutdown = preserveManagedInstancesOnShutdown,
            AvoidSimultaneousScheduledRestarts = avoidSimultaneousScheduledRestarts,
            AgentOfflineShutdownSeconds = agentOfflineShutdownSeconds,
            AgentReconnectIntervalSeconds = agentReconnectIntervalSeconds,
            AgentReconnectJitterSeconds = agentReconnectJitterSeconds,
            LauncherToken = launcherToken,
        };
    }
}
