using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services;
using Quasar.Services.Analytics;
using Quasar.Services.PluginSdk;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterAgentIdentityTests : IDisposable
{
    private readonly MetricsStoreService _metrics = new(null!, new AnalyticsStoreOptions(), NullLogger<MetricsStoreService>.Instance);
    private AgentRegistry Registry() => new(null!, _metrics, new ProfilerStoreService(), new PluginStatsStoreService());
    private static AgentHello Hello(long epoch = 300, string id = "process-a") => new()
    { AgentId = id, UniqueName = "slot-server", ClusterMode = true, ClusterId = "cluster-a", ClusterSlot = "slot-a", ClusterNodeId = "node-a", ClusterEpoch = epoch };
    private static AgentSnapshot Snapshot(long epoch = 300, string id = "process-a") => new()
    { AgentId = id, UniqueName = "slot-server", ClusterMode = true, ClusterId = "cluster-a", ClusterSlot = "slot-a", ClusterNodeId = "node-a", ClusterEpoch = epoch };
    private static Task Sender(AgentWireMessage message, CancellationToken token) => Task.CompletedTask;

    [Fact]
    public void ReconnectDropsSnapshotAndConfigAndRejectsSupersededConnection()
    {
        var registry = Registry();
        var configs = new PluginConfigService(registry, NullLogger<PluginConfigService>.Instance);
        registry.UpsertHello(Hello(), "old", Sender);
        registry.UpdateSnapshot(Snapshot(), "old");
        configs.IngestSnapshot(new() { AgentId = "process-a", Plugins = [new() { PluginId = "old-config" }] }, "old");
        Assert.True(configs.HasConfigs("process-a"));

        registry.UpsertHello(Hello(), "new", Sender);
        Assert.Null(registry.GetAgents().Single().Snapshot);
        Assert.False(configs.HasConfigs("process-a"));
        registry.UpdateSnapshot(Snapshot(), "old");
        configs.IngestSnapshot(new() { AgentId = "process-a", Plugins = [new() { PluginId = "late-config" }] }, "old");
        registry.MarkDisconnected("old");
        Assert.True(registry.GetAgents().Single().IsConnected);
        Assert.Null(registry.GetAgents().Single().Snapshot);
        Assert.False(configs.HasConfigs("process-a"));
        Assert.False(registry.TryGetTelemetryKey("old", out _));
    }

    [Fact]
    public void CompleteIncarnationChangesTelemetryKeyAndRequiresExactRegistryMatch()
    {
        var old = new AgentRuntimeState { AgentId = "old", Hello = Hello(300, "old") };
        var current = new AgentRuntimeState { AgentId = "new", Hello = Hello(301, "new") };
        Assert.NotEqual(old.TelemetryKey, current.TelemetryKey);
        var node = new Admin.NodeStatus("node-a", "slot-a", 301, Admin.NodeRole.Regular, Admin.NodeState.Active,
            "endpoint", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), 0, 0, "host", true);
        Assert.Same(current, ClusterFleetService.MatchAgent("cluster-a", node, [old, current]));
        Assert.Null(ClusterFleetService.MatchAgent("other-cluster", node, [current]));
        Assert.Null(ClusterFleetService.MatchAgent("cluster-a", node with { SlotKey = "other-slot" }, [current]));
        Assert.Null(ClusterFleetService.MatchAgent("cluster-a", node, [current, current.Clone()]));
        current.Hello!.ClusterEpoch = 0;
        Assert.Null(ClusterFleetService.MatchAgent("cluster-a", node, [current]));
    }

    [Fact]
    public void ClusterAgentCannotSignalStandaloneLifecycleOrChangeEpochOnConnection()
    {
        var registry = Registry();
        registry.UpsertHello(Hello(), "connection", Sender);
        Assert.False(registry.TryGetUniqueName("connection", out _));
        registry.UpdateSnapshot(Snapshot(301), "connection");
        Assert.Null(registry.GetAgents().Single().Snapshot);
        registry.UpdateSnapshot(Snapshot(), "connection");
        Assert.Equal(300, registry.GetAgents().Single().ClusterEpoch);
    }

    [Fact]
    public void UnknownEpochCanBeCompletedButCannotLaterChange()
    {
        var registry = Registry();
        registry.UpsertHello(Hello(0), "connection", Sender);
        registry.UpdateSnapshot(Snapshot(300), "connection");
        registry.UpdateSnapshot(Snapshot(301), "connection");
        Assert.Equal(300, registry.GetAgents().Single().ClusterEpoch);
    }

    [Fact]
    public async Task ReplacedConnectionCannotCompletePendingCommands()
    {
        var registry = Registry();
        registry.UpsertHello(Hello(), "old", Sender);
        var command = new ServerCommandEnvelope { AgentId = "process-a", CommandId = "command-a" };
        var result = registry.SendCommandAndWaitAsync(command, TimeSpan.FromSeconds(5));
        registry.UpsertHello(Hello(), "new", Sender);
        await Assert.ThrowsAsync<InvalidOperationException>(() => result);
        registry.UpdateCommandResult(new() { AgentId = "process-a", CommandId = "command-a" }, "old");
        Assert.Empty(registry.GetAgents().Single().CommandResults);
    }
    [Fact]
    public async Task StalePluginEditorCannotWriteToReplacementConnection()
    {
        var registry = Registry();
        int sends = 0;
        registry.UpsertHello(Hello(), "old", Sender);
        registry.UpsertHello(Hello(), "new", (_, _) => { sends++; return Task.CompletedTask; });
        var configs = new PluginConfigService(registry, NullLogger<PluginConfigService>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            configs.UpdatePluginConfigAsync("process-a", "plugin-a", "{}", "old"));
        Assert.Equal(0, sends);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            configs.UpdatePluginConfigAsync("process-a", "plugin-a", "{}", "new"));
        Assert.Equal(0, sends);
    }

    public void Dispose() => _metrics.Dispose();
}
