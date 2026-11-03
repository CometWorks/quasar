using System.Reflection;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

[Collection("Quasar shutdown")]
public sealed class QuasarLoggingConfiguratorTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    public void ConsoleOptInWritesOnceAlongsideFileLogging(string? value, bool enabled)
    {
        var previous = Environment.GetEnvironmentVariable("QUASAR_CONSOLE_LOGGING");
        var stdout = Console.Out;
        using var output = new StringWriter();
        var directory = Path.Combine(Path.GetTempPath(), "quasar-console-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable("QUASAR_CONSOLE_LOGGING", value);
            Console.SetOut(output);
            var configuration = (LoggingConfiguration)typeof(QuasarLoggingConfigurator)
                .GetMethod("BuildConfiguration", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [new WebServiceOptions { LoggingDirectory = directory }])!;
            Assert.Equal(enabled ? 1 : 0, configuration.AllTargets.OfType<AsyncTargetWrapper>()
                .Count(wrapper => wrapper.WrappedTarget is ConsoleTarget));
            using var factory = new LogFactory { Configuration = configuration };
            factory.GetLogger("Quasar.JournalTest").Warn("Unique journal warning");
            factory.Flush(TimeSpan.FromSeconds(5));
            Assert.Equal(enabled ? 1 : 0, output.ToString().Split("Unique journal warning").Length - 1);
            Assert.Equal(1, File.ReadAllText(Path.Combine(directory, "quasar.log")).Split("Unique journal warning").Length - 1);
        }
        finally
        {
            Console.SetOut(stdout);
            Environment.SetEnvironmentVariable("QUASAR_CONSOLE_LOGGING", previous);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void AsyncSinksPreserveMainAndDiscordRouting()
    {
        string directory = Path.Combine(Path.GetTempPath(), "quasar-async-logging-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = (LoggingConfiguration)typeof(QuasarLoggingConfigurator)
                .GetMethod("BuildConfiguration", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [new WebServiceOptions { LoggingDirectory = directory, LoggingMinimumLevel = "Warn" }])!;
            foreach (var target in configuration.LoggingRules.SelectMany(rule => rule.Targets))
            {
                var wrapper = Assert.IsType<AsyncTargetWrapper>(target);
                Assert.Equal(4096, wrapper.QueueLimit);
                Assert.Equal(AsyncTargetWrapperOverflowAction.Discard, wrapper.OverflowAction);
            }
            using var factory = new LogFactory { Configuration = configuration };
            factory.GetLogger("Quasar.Test").Warn("Main log entry");
            factory.GetLogger("Quasar.Services.Discord.Test").Error("Discord log entry");
            factory.Flush(TimeSpan.FromSeconds(5));
            string main = File.ReadAllText(Path.Combine(directory, "quasar.log"));
            string discord = File.ReadAllText(Path.Combine(directory, "discord.log"));
            Assert.Contains("Main log entry", main);
            Assert.Contains("Discord log entry", main);
            Assert.Contains("Discord log entry", discord);
            Assert.DoesNotContain("Main log entry", discord);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
