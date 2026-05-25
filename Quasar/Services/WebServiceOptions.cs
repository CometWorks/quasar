using Magnetar.Protocol.Runtime;
using System.Reflection;

namespace Quasar.Services;

public sealed class WebServiceOptions
{
    public const string SupervisorName = "Quasar";

    public string Host { get; init; } = "127.0.0.1";

    public int Port { get; init; } = 58631;

    public string InstanceId { get; init; } = Guid.NewGuid().ToString("N");

    public string NodeId { get; init; } = Environment.MachineName.ToLowerInvariant();

    public string NodeName { get; init; } = Environment.MachineName;

    public string BaseUrl { get; init; } = "http://127.0.0.1:58631";

    public string ListenUrl { get; init; } = "http://127.0.0.1:58631";

    public string Version { get; init; } = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";

    public string Mode { get; init; } = "Console";

    public bool OpenBrowserOnStart { get; init; } = true;

    public string LoggingDirectory { get; init; } = MagnetarPaths.GetQuasarLogDirectory();

    public string LoggingFormat { get; init; } = "text";

    public string LoggingMinimumLevel { get; init; } = "Info";

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
                   ?? "127.0.0.1";

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

        var advertisedHost = host switch
        {
            "0.0.0.0" => "127.0.0.1",
            "*" => "127.0.0.1",
            "+" => "127.0.0.1",
            _ => host,
        };

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
            BaseUrl = $"http://{advertisedHost}:{port}",
            ListenUrl = $"http://{host}:{port}",
        };
    }
}
