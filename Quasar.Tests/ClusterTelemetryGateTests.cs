using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using PluginSdk.Clustering;
using Quasar.Agent;
using Quasar.Services;
using Quasar.Services.Analytics;
using Xunit;

namespace Quasar.Tests;

[Collection("Cluster identity environment")]
public sealed class ClusterTelemetryGateTests
{
    private static readonly AgentOptions Cluster = new() { ClusterMode = true };

    [Fact]
    public void SpareIsPausedUntilItOwnsAPartitionAndAgainAfterItLosesIt()
    {
        var provider = new FakeProvider();
        Assert.True(PluginCluster.Register(provider));
        try
        {
            Assert.True(ClusterTelemetryGate.IsPaused(Cluster));
            Assert.False(ClusterTelemetryGate.IsPaused(new AgentOptions()));

            provider.Context.OwnedPartitions[42] = 1;
            Assert.False(ClusterTelemetryGate.IsPaused(Cluster));

            provider.Context.OwnedPartitions.Clear();
            Assert.True(ClusterTelemetryGate.IsPaused(Cluster));

            provider.Context.Role = "WorldAuthority";
            Assert.False(ClusterTelemetryGate.IsPaused(Cluster));
        }
        finally { PluginCluster.Unregister(provider); }
    }

    [Fact]
    public void StartingClusterNodeWithoutProviderIsPausedButStandaloneIsNot()
    {
        Assert.True(ClusterTelemetryGate.IsPaused(Cluster));
        Assert.False(ClusterTelemetryGate.IsPaused(new AgentOptions()));
    }

    [Fact]
    public void PausedSnapshotKeepsLiveStatusButRecordsNoAnalytics()
    {
        using var metrics = new MetricsStoreService(null!, new AnalyticsStoreOptions(), NullLogger<MetricsStoreService>.Instance);
        var registry = new AgentRegistry(null!, metrics, new ProfilerStoreService(), new PluginStatsStoreService());
        AgentSnapshot Snapshot(string id, bool paused) => new()
        { AgentId = id, UniqueName = "slot-server", ClusterMode = true, ClusterId = "c", ClusterSlot = "s", ClusterNodeId = id, ClusterEpoch = 1, TelemetryPaused = paused };
        foreach (var id in new[] { "spare", "active" })
            registry.UpsertHello(new AgentHello { AgentId = id, UniqueName = "slot-server", ClusterMode = true, ClusterId = "c", ClusterSlot = "s", ClusterNodeId = id, ClusterEpoch = 1 },
                id, (_, _) => Task.CompletedTask);

        registry.UpdateSnapshot(Snapshot("spare", true), "spare");
        registry.UpdateSnapshot(Snapshot("active", false), "active");

        var agents = registry.GetAgents().ToDictionary(a => a.AgentId);
        Assert.NotNull(agents["spare"].Snapshot);
        Assert.Equal([agents["active"].TelemetryKey], metrics.GetUniqueNames());

        registry.UpdateSnapshot(Snapshot("spare", false), "spare");
        Assert.Contains(agents["spare"].TelemetryKey, metrics.GetUniqueNames());
    }

    private sealed class FakeProvider : IPluginClusterProvider
    {
        public NodeContext Context { get; } = new() { ClusterId = "c", Node = "spare", Role = "Regular", Available = true };
        public event Action? ContextChanged { add { } remove { } }
        public Task<PluginResult> ExecuteAsync(PluginRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PluginResult> RequestAsync(string plugin, PluginTarget target, string topic, byte[] payload, Guid operationId, TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public IDisposable RegisterHandler(string plugin, string topic, Func<PluginMessage, Task<byte[]>> handler) => throw new NotSupportedException();
    }
}
