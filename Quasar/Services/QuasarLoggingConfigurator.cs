using Magnetar.Protocol.Runtime;
using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using NLog.Web;

namespace Quasar.Services;

public static class QuasarLoggingConfigurator
{
    public static void Configure(WebApplicationBuilder builder, WebServiceOptions options)
    {
        Directory.CreateDirectory(options.LoggingDirectory);

        LogManager.Configuration = BuildConfiguration(options);
        builder.Logging.ClearProviders();
        builder.Host.UseNLog();
    }

    private static LoggingConfiguration BuildConfiguration(WebServiceOptions options)
    {
        var configuration = new LoggingConfiguration();

        var fileName = Path.Combine(
            string.IsNullOrWhiteSpace(options.LoggingDirectory)
                ? MagnetarPaths.GetQuasarLogDirectory()
                : options.LoggingDirectory,
            "quasar.log");

        var fileTarget = new FileTarget("quasar-file")
        {
            FileName = fileName,
            KeepFileOpen = false,
            CreateDirs = true,
            Layout = BuildLayout(options.LoggingFormat),
        };

        configuration.AddTarget(fileTarget);
        configuration.AddRule(ParseMinimumLevel(options.LoggingMinimumLevel), NLog.LogLevel.Fatal, fileTarget);
        return configuration;
    }

    private static Layout BuildLayout(string? loggingFormat)
    {
        if (string.Equals(loggingFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonLayout
            {
                Attributes =
                {
                    new JsonAttribute("timestamp", "${longdate}"),
                    new JsonAttribute("level", "${level:uppercase=true}"),
                    new JsonAttribute("logger", "${logger}"),
                    new JsonAttribute("message", "${message}"),
                    new JsonAttribute("exception", "${exception:format=tostring}"),
                },
            };
        }

        return new SimpleLayout(
            "${longdate} [${level:uppercase=true}] (${threadid}) ${logger}: ${message:withexception=true}");
    }

    private static NLog.LogLevel ParseMinimumLevel(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            try
            {
                return NLog.LogLevel.FromString(value);
            }
            catch
            {
            }
        }

        return NLog.LogLevel.Info;
    }
}
