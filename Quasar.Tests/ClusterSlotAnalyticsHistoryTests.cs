using System.Reflection;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Runtime;
using Magnetar.Protocol.Transport;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services;
using Quasar.Services.Analytics;
using Xunit;

namespace Quasar.Tests;

// A cluster slot's chart history has to outlive node recycles and Quasar restarts.
[Collection("Exact server creation")]
public sealed class ClusterSlotAnalyticsHistoryTests
{
    private static Task Sender(AgentWireMessage message, CancellationToken token) =>
        Task.CompletedTask;

    private static void RunNode(
        AgentRegistry registry,
        string agentId,
        string node,
        long epoch,
        long fromUnix,
        int samples
    )
    {
        registry.UpsertHello(
            new AgentHello
            {
                AgentId = agentId,
                UniqueName = "slot-server",
                HostId = "host",
                ClusterMode = true,
                ClusterId = "cluster-a",
                ClusterSlot = "local-node-1",
                ClusterNodeId = node,
                ClusterEpoch = epoch,
            },
            agentId,
            Sender
        );
        for (var i = 0; i < samples; i++)
            registry.UpdateSnapshot(
                new AgentSnapshot
                {
                    AgentId = agentId,
                    UniqueName = "slot-server",
                    HostId = "host",
                    ClusterMode = true,
                    ClusterId = "cluster-a",
                    ClusterSlot = "local-node-1",
                    ClusterNodeId = node,
                    ClusterEpoch = epoch,
                    IsRunning = true,
                    CapturedAtUtc = DateTimeOffset.FromUnixTimeSeconds(fromUnix + 2 * i),
                    Metrics = new ServerMetrics { SimSpeed = 0.5f },
                },
                agentId
            );
        registry.MarkDisconnected(agentId);
    }

    private static async Task<int> WaitForSamplesAsync(
        MetricsStoreService metrics,
        string key,
        int expected
    )
    {
        for (var i = 0; i < 200 && (metrics.GetStore(key)?.Raw.Count ?? 0) < expected; i++)
            await Task.Delay(10);
        return metrics.GetStore(key)?.Raw.Count ?? 0;
    }

    [Fact]
    public async Task SlotHistorySurvivesNodeRecyclesAndAQuasarRestart()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "quasar-slot-history-" + Guid.NewGuid().ToString("N")
        );
        var cache = typeof(MagnetarPaths).GetField(
            "_cachedQuasarDirectory",
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        var oldRoot = cache.GetValue(null);
        cache.SetValue(null, root);
        try
        {
            var start = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600;
            string slotKey;
            using (
                var catalog = new DedicatedServerCatalog(
                    NullLogger<DedicatedServerCatalog>.Instance
                )
            )
            using (
                var metrics = new MetricsStoreService(
                    catalog,
                    new AnalyticsStoreOptions(),
                    NullLogger<MetricsStoreService>.Instance
                )
            )
            {
                await metrics.StartAsync(default);
                var registry = new AgentRegistry(
                    null!,
                    metrics,
                    new ProfilerStoreService(),
                    new PluginStatsStoreService()
                );
                RunNode(registry, "first", "node-a", 300, start, 50);
                RunNode(registry, "second", "node-b", 301, start + 200, 50);
                var agents = registry.GetAgents();
                slotKey = agents[0].AnalyticsKey;
                Assert.All(agents, agent => Assert.Equal(slotKey, agent.AnalyticsKey));

                // One store per slot, none per node process.
                Assert.Equal(100, await WaitForSamplesAsync(metrics, slotKey, 100));
                Assert.All(agents, agent => Assert.Null(metrics.GetStore(agent.TelemetryKey)));
                await metrics.StopAsync(default);
            }

            // A restarted Quasar has no agents yet; the slot's history is read back from disk.
            using (
                var catalog = new DedicatedServerCatalog(
                    NullLogger<DedicatedServerCatalog>.Instance
                )
            )
            using (
                var metrics = new MetricsStoreService(
                    catalog,
                    new AnalyticsStoreOptions(),
                    NullLogger<MetricsStoreService>.Instance
                )
            )
            {
                await metrics.StartAsync(default);
                var registry = new AgentRegistry(
                    null!,
                    metrics,
                    new ProfilerStoreService(),
                    new PluginStatsStoreService()
                );
                var chart = Assert.Single(
                    new AnalyticsSeriesService(metrics, new(), new(), registry)
                        .Build(start, start + 300, [slotKey], ["simspeed"], 1000)
                        .Charts
                );
                var series = Assert.Single(chart.Series);
                Assert.Equal(slotKey, series.UniqueName);
                Assert.Equal(100, series.Y.Count(value => value is not null));

                // The slot's next node keeps appending to the same history.
                RunNode(registry, "third", "node-c", 302, start + 400, 10);
                Assert.Equal(110, await WaitForSamplesAsync(metrics, slotKey, 110));
                await metrics.StopAsync(default);
            }
        }
        finally
        {
            cache.SetValue(null, oldRoot);
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
