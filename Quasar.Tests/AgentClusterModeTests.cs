using Magnetar.Protocol.Transport;
using Quasar.Agent;
using Xunit;

namespace Quasar.Tests;

[Collection("Cluster identity environment")]
public sealed class AgentClusterModeTests
{
    [Fact]
    public void ReleaseEnvironmentEnablesLifecycleSafetyWithoutInventingAnIncarnation()
    {
        var values = new Dictionary<string, string?>
        {
            ["CLUSTER_GATEWAY_REGISTRY"] = "http://127.0.0.1:29416",
            ["CLUSTER_ID"] = "release-cluster", ["CLUSTER_NODE_ID"] = "wa-1",
            ["CLUSTER_NODE_ROLE"] = "WA", ["CLUSTER_NODE_EPOCH"] = "7",
            ["CLUSTER_SLOT_ID"] = null,
        };
        var previous = values.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (key, value) in values) Environment.SetEnvironmentVariable(key, value);
            var options = AgentOptions.FromEnvironment();
            Assert.True(options.ClusterMode);
            Assert.Equal("release-cluster", options.ClusterId);
            Assert.Equal("wa-1", options.ClusterNodeId);
            Assert.Equal("WA", options.ClusterNodeRole);
            Assert.Equal(0, options.ClusterEpoch);
            Assert.Empty(options.ClusterSlot);
            Assert.False(options.ShouldSelfStop(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow));
            Assert.False(options.AllowsCommand(ServerCommandType.StopServer));
            Assert.False(options.AllowsCommand(ServerCommandType.SaveWorld));
        }
        finally { foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value); }
    }

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
