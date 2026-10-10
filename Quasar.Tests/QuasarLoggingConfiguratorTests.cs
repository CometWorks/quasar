using System.Reflection;
using NLog;
using NLog.Config;
using NLog.Targets.Wrappers;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

public sealed class QuasarLoggingConfiguratorTests
{
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
