using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services;
using Quasar.Services.Analytics;
using Quasar.Services.Discord;
using Quasar.Services.PluginSdk;
using Xunit;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Tests;

public sealed class DiscordClusterBridgeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly DiscordServerOptions Settings = new() { UniqueName = "cluster-a", IsCluster = true };

    [Fact]
    public void ClusterAndStandaloneSettingsRemainDistinctWhenNamesMatch()
    {
        var options = DiscordOptions.Normalize(new() { Servers = [Settings, new() { UniqueName = "cluster-a" }] });
        Assert.Equal(2, options.Servers.Select(s => s.TargetKey).Distinct().Count());
        Assert.True(options.Servers.Single(s => s.IsCluster).Clone().IsCluster);
        Assert.Equal("cluster:cluster-a", options.Servers.Single(s => s.IsCluster).TargetKey);
    }

    [Fact]
    public void ChatCursorHandlesPollingOverlapIdenticalTimestampsAndAuthorityReplacement()
    {
        var cursor = new DiscordClusterBridge.ChatCursor();
        var first = new Admin.ChatMessage(20, Now, 10, 0, "one");
        var second = first with { Seq = 21, Text = "two" };
        Assert.Equal(2, cursor.Observe(new([first, second], 21, 5), Now.AddSeconds(-1)).Count);
        Assert.Empty(cursor.Observe(new([first, second], 21, 5), Now.AddSeconds(-1)));
        var replacement = first with { Seq = 1, Text = "new WA" };
        Assert.Single(cursor.Observe(new([replacement], 1, 6), Now.AddSeconds(-1)));
        Assert.Equal(1, cursor.Next);
        Assert.Equal(6, cursor.Epoch);
    }

    [Fact]
    public void ChatCursorSkipsOldHistoryAndDoesNotAdvanceOnMissingAuthority()
    {
        var cursor = new DiscordClusterBridge.ChatCursor();
        Assert.Empty(cursor.Observe(new([new(1, Now.AddHours(-1), 10, 0, "old")], 1, 5), Now));
        Assert.Empty(cursor.Observe(new([], 999, 0), Now));
        Assert.Equal(1, cursor.Next);
        Assert.Equal(5, cursor.Epoch);
    }

    [Fact]
    public void MatchesEveryHostButRejectsOldEpochsAmbiguousClaimsStaleAndForeignAgents()
    {
        var a = Agent("a", 3, "host-1");
        var b = Agent("b", 4, "host-2");
        var old = Agent("a", 2, "host-1");
        var foreign = Agent("b", 4, "host-2");
        foreign.Snapshot!.ClusterId = "other";
        var status = Status(Node("a", 3, "host-1"), Node("b", 4, "host-2"));
        Assert.Equal([a, b], DiscordClusterBridge.MatchAgents(status, [old, foreign, a, b]));
        Assert.Equal([b], DiscordClusterBridge.MatchAgents(status, [a, a.Clone(), b]));
        a.LastSnapshotReceivedUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.Equal([b], DiscordClusterBridge.MatchAgents(status, [a, b]));
        Assert.Empty(DiscordClusterBridge.MatchAgents(status with { Nodes = [status.Nodes[1] with { LeaseFresh = false }] }, [b]));
    }

    [Fact]
    public void CrossHostPlayerMoveDoesNotProduceJoinLeaveNotifications()
    {
        var before = new Admin.ClientSummary(123, "Player", 42, "Admitted", false, "a", 1, Now, Now);
        var after = before with { Node = "b", Partition = 2 };
        Assert.Empty(DiscordClusterBridge.StatusMessages(Settings, Status(), [before], Status(), [after]));
        Assert.Single(DiscordClusterBridge.StatusMessages(Settings, Status(), [], Status(), [after]));
        Assert.Single(DiscordClusterBridge.StatusMessages(Settings, Status(), [before], Status(), []));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ChatUsesCurrentAuthorityAndRequiresCapabilitiesAndAcknowledgement(bool ready, bool success)
    {
        var directory = Path.Combine(Path.GetTempPath(), "discord-cluster-" + Guid.NewGuid());
        using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = directory }).Build());
        using var metrics = new MetricsStoreService(null!, new AnalyticsStoreOptions(), NullLogger<MetricsStoreService>.Instance);
        var registry = new AgentRegistry(null!, metrics, new ProfilerStoreService(), new PluginStatsStoreService());
        var sent = new List<ServerCommandEnvelope>();
        try
        {
            await catalog.CreateAsync(new("cluster-a", "Cluster", "http://gateway.test", ""), default);
            foreach (var state in new[] { Agent("wa", 7, "host-1"), Agent("b", 8, "host-2") })
            {
                var snapshot = state.Snapshot!;
                snapshot.ClusterChatReady = ready || snapshot.ClusterNodeId == "wa";
                registry.UpsertHello(new() { AgentId = snapshot.AgentId, UniqueName = snapshot.UniqueName, ClusterMode = true,
                    ClusterId = snapshot.ClusterId, ClusterSlot = snapshot.ClusterSlot, ClusterNodeId = snapshot.ClusterNodeId,
                    ClusterEpoch = snapshot.ClusterEpoch }, state.AgentId, (wire, _) =>
                {
                    sent.Add(wire.Command!);
                    registry.UpdateCommandResult(new() { CommandId = wire.Command!.CommandId, AgentId = wire.Command.AgentId,
                        Success = success, Message = "Partial delivery" }, state.AgentId);
                    return Task.CompletedTask;
                });
                registry.UpdateSnapshot(snapshot, state.AgentId);
            }
            var status = Status(Node("wa", 7, "host-1") with { Role = Admin.NodeRole.WorldAuthority }, Node("b", 8, "host-2"))
                with { WorldAuthority = new("wa", 7, 1, Now.AddMinutes(1)) };
            using var http = new HttpClient(new GatewayHandler(status));
            var bridge = new DiscordClusterBridge(catalog, new ClusterGatewayClient(http), null!, registry, null!, null!,
                NullLogger<DiscordClusterBridge>.Instance);
            if (ready && success) await bridge.SendCommandAsync(Settings, ServerCommandType.SendChat, "hello");
            else await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.SendCommandAsync(Settings, ServerCommandType.SendChat, "hello"));
            Assert.Equal(ready ? 1 : 0, sent.Count);
            if (ready) Assert.Equal("wa-7", sent.Single().AgentId);
        }
        finally { catalog.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PresenceWithoutChannelBindingsCountsClusterOnceUsingGatewayPlayers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "discord-presence-" + Guid.NewGuid());
        using var catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = directory }).Build());
        try
        {
            await catalog.CreateAsync(new("cluster-a", "Cluster", "http://gateway.test", ""), default);
            var status = Status(Node("wa", 7, "host-1"), Node("a", 8, "host-1"), Node("b", 9, "host-2"));
            status = status with { Counts = status.Counts with { ConnectedClients = 12 } };
            using var http = new HttpClient(new GatewayHandler(status));
            var bridge = new DiscordClusterBridge(catalog, new ClusterGatewayClient(http), null!, null!, null!, null!,
                NullLogger<DiscordClusterBridge>.Instance);
            // No Discord client or relay dependencies are needed for presence-only observation.
            await bridge.PollAsync(null!, new DiscordOptions(), default);
            var server = Assert.Single(bridge.PresenceServers());
            Assert.Equal("cluster:cluster-a", server.Key);
            Assert.Equal(12, server.Players);
            Assert.True(server.Online);
            Assert.Equal("1/1 servers online, 12 players", DiscordPresence.Build(new(), [server]).Activity);
        }
        finally { catalog.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static Admin.NodeStatus Node(string name, long epoch, string host) => new(name, name, epoch,
        Admin.NodeRole.Regular, Admin.NodeState.Active, "endpoint", null, Now, Now.AddMinutes(1), 0, 0, host, true);
    private static AgentRuntimeState Agent(string node, long epoch, string host) => new()
    {
        AgentId = $"{node}-{epoch}", IsConnected = true, LastSnapshotReceivedUtc = DateTimeOffset.UtcNow,
        Snapshot = new() { AgentId = $"{node}-{epoch}", UniqueName = "slot-" + node, ClusterMode = true,
            ClusterId = "cluster-a", ClusterSlot = node, ClusterNodeId = node, ClusterEpoch = epoch, HostId = host },
    };
    private static Admin.ClusterStatus Status(params Admin.NodeStatus[] nodes) => new("cluster-a", "world",
        Admin.ClusterPhase.Serving, default, null, null, false, false, [], new(0, nodes.Length, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        new(null, 0, 0, Now), nodes, [], true, Admin.AdminHealth.Healthy, [], Now);
    private sealed class GatewayHandler(Admin.ClusterStatus status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.EndsWith("/status", request.RequestUri!.AbsolutePath);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(
                new Admin.AdminEnvelope<Admin.ClusterStatus>(Admin.AdminProtocol.Version, Now, status),
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } })) };
            response.Headers.Add("X-Cluster-Gateway-Protocol", Admin.AdminProtocol.Version.ToString());
            return Task.FromResult(response);
        }
    }
}
