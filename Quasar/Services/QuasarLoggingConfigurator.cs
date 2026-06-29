using Magnetar.Protocol.Runtime;
using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using NLog.Web;

namespace Quasar.Services;

public static class QuasarLoggingConfigurator
{
    public static string ResolveDiscordLogPath(WebServiceOptions options)
    {
        return Path.Combine(
            string.IsNullOrWhiteSpace(options.LoggingDirectory)
                ? MagnetarPaths.GetQuasarLogDirectory()
                : options.LoggingDirectory,
            "discord.log");
    }

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

        var discordFileTarget = new FileTarget("discord-file")
        {
            FileName = ResolveDiscordLogPath(options),
            KeepFileOpen = false,
            CreateDirs = true,
            Layout = BuildLayout(options.LoggingFormat),
        };

        var minimumLevel = ParseMinimumLevel(options.LoggingMinimumLevel);

        configuration.AddTarget(fileTarget);
        configuration.AddTarget(discordFileTarget);
        configuration.AddRule(minimumLevel, NLog.LogLevel.Fatal, fileTarget);
        configuration.LoggingRules.Add(new LoggingRule("Quasar.Services.Discord.*", minimumLevel, NLog.LogLevel.Fatal, discordFileTarget));

        // Bootstrap launches the worker with QUASAR_CONSOLE_LOGGING=true and drains
        // stdout/stderr so worker warnings and errors reach the Bootstrap host console
        // (systemd journal on Linux, the Bootstrap process console on Windows).
        if (IsConsoleLoggingRequested())
        {
            var consoleTarget = new ConsoleTarget("quasar-console")
            {
                Layout = BuildLayout(options.LoggingFormat),
                StdErr = false,
            };

            configuration.AddTarget(consoleTarget);
            configuration.AddRule(minimumLevel, NLog.LogLevel.Fatal, consoleTarget);
        }

        return configuration;
    }

    private static bool IsConsoleLoggingRequested()
    {
        var value = Environment.GetEnvironmentVariable("QUASAR_CONSOLE_LOGGING");
        return !string.IsNullOrWhiteSpace(value) &&
               (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "1", StringComparison.Ordinal));
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
