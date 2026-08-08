using Magnetar.Protocol.Transport;
using Quasar.Agent;
using Xunit;

namespace Quasar.Tests;

public sealed class AgentClusterModeTests
{
    [Fact]
    public void ClusterModeNeverSelfStopsWhenQuasarIsOffline()
    {
        var options = new AgentOptions { ClusterMode = true, OfflineShutdownSeconds = 0 };
        DateTime disconnected = DateTime.UtcNow.AddDays(-1);

        Assert.False(options.ShouldSelfStop(disconnected, DateTime.UtcNow));
    }

    [Fact]
    public void StandaloneModeKeepsConfiguredOfflineShutdown()
    {
        var options = new AgentOptions { OfflineShutdownSeconds = 60 };
        DateTime disconnected = DateTime.UtcNow;

        Assert.False(options.ShouldSelfStop(disconnected, disconnected.AddSeconds(59)));
        Assert.True(options.ShouldSelfStop(disconnected, disconnected.AddSeconds(60)));
    }

    [Theory]
    [InlineData(ServerCommandType.SaveWorld)]
    [InlineData(ServerCommandType.StopServer)]
    public void ClusterModeRejectsAgentLocalLifecycleCommands(ServerCommandType command)
    {
        Assert.False(new AgentOptions { ClusterMode = true }.AllowsCommand(command));
        Assert.True(new AgentOptions().AllowsCommand(command));
    }
}
