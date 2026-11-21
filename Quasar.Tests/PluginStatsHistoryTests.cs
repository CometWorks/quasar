using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services;
using Quasar.Services.Analytics;
using Xunit;
using Xunit.Abstractions;

namespace Quasar.Tests;

// SE-1820: plugin statistics history per label, kept per cluster, rolled up by each field's hints,
// bounded on disk and reloaded after a Quasar restart.
public sealed class PluginStatsHistoryTests(ITestOutputHelper output) : IDisposable
{
    private const string Provider = "client-lag";
    private const string Group = "ClientLagRow";
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "quasar-plugin-history-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public void FoldFollowsEachFieldsOverTimeHint()
    {
        HistorySample[] samples = [new(0, 1, 4), new(10, 1, 1), new(20, 2, 10)];
        double Fold(string kind, string overTime) =>
            PluginStatsHistoryService.Fold(new HistoryField("x", kind, "", "", overTime), samples);

        Assert.Equal(6.25, Fold("gauge", "Mean")); // weighted by row count: (4 + 1 + 2 * 10) / 4
        Assert.Equal(10, Fold("gauge", "Max"));
        Assert.Equal(1, Fold("gauge", "Min"));
        Assert.Equal(15, Fold("counter", "Sum"));
        Assert.Equal(10, Fold("gauge", "Last"));
        Assert.Equal(4, Fold("discrete", "Median"));
        Assert.Equal(4, Fold("gauge", "Percentiles"));

        // No hint: the SDK default of the kind.
        Assert.Equal(15, Fold("counter", ""));
        Assert.Equal(4, Fold("discrete", ""));
        Assert.Equal(6.25, Fold("gauge", ""));
    }

    [Fact]
    public async Task RetentionKeepsRawFor48HoursMinutesFor7DaysAndHoursForRetentionDays()
    {
        using var history = Create(new AnalyticsStoreOptions());
        history.Record("server", Lag(Now.AddHours(-47), ("A", 1, 1, 1)));
        for (var i = 0; i < 6; i++) // one minute, 49 hours ago
            history.Record("server", Lag(Now.AddHours(-49).AddSeconds(10 * i), ("A", i, i, 1)));
        history.Record("server", Lag(Now.AddDays(-8), ("A", 10, 30, 2))); // two minutes of one hour, 8 days ago
        history.Record("server", Lag(Now.AddDays(-8).AddMinutes(5), ("A", 20, 40, 3)));
        history.Record("server", Lag(Now.AddDays(-31), ("A", 99, 99, 99))); // past RetentionDays

        await history.PersistAsync(compact: true);

        Assert.Equal([1, 1, 1], history.CountRows("server", Provider, Group, "A"));
        var p99 = Read(history, "server", "A", "P99Ms");
        Assert.Equal(
            [
                new HistorySample(Now.AddDays(-8).ToUnixTimeSeconds(), 2, 40), // Max
                new HistorySample(Now.AddHours(-49).ToUnixTimeSeconds(), 6, 5), // Max
                new HistorySample(Now.AddHours(-47).ToUnixTimeSeconds(), 1, 1),
            ],
            p99
        );
        Assert.Equal([15, 2.5, 1], Read(history, "server", "A", "P50Ms").Select(s => s.Value)); // Mean
        Assert.Equal([5.0, 6, 1], Read(history, "server", "A", "Hitches100").Select(s => s.Value)); // Sum
    }

    [Fact]
    public async Task ShorterConfiguredLimitsMoveTheCutoffs()
    {
        var options = AnalyticsStoreOptions.Create(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Quasar:Analytics:PluginStatsRawRetentionHours"] = "2",
                        ["Quasar:Analytics:PluginStatsMinuteRetentionDays"] = "1",
                    }
                )
                .Build()
        );
        Assert.Equal(2, options.PluginStatsRawRetentionHours);
        Assert.Equal(1, options.PluginStatsMinuteRetentionDays);
        var defaults = AnalyticsStoreOptions.Create(new ConfigurationBuilder().Build());
        Assert.Equal(48, defaults.PluginStatsRawRetentionHours);
        Assert.Equal(7, defaults.PluginStatsMinuteRetentionDays);

        using var history = Create(options);
        history.Record("server", Lag(Now.AddHours(-1), ("A", 1, 1, 1))); // raw
        history.Record("server", Lag(Now.AddHours(-3), ("A", 1, 1, 1))); // minute
        history.Record("server", Lag(Now.AddHours(-30), ("A", 1, 1, 1))); // hour
        await history.PersistAsync(compact: true);

        Assert.Equal([1, 1, 1], history.CountRows("server", Provider, Group, "A"));
    }

    [Fact]
    public async Task HistorySurvivesARestartAndIsWrittenInBatches()
    {
        var options = new AnalyticsStoreOptions();
        long[] times =
        [
            .. Enumerable.Range(0, 4).Select(i => Now.AddMinutes(-10 + i).ToUnixTimeSeconds()),
        ];
        var path = string.Empty;
        using (var history = Create(options))
        {
            await history.StartAsync(default);
            for (var i = 0; i < 3; i++)
                history.Record(
                    "server",
                    Lag(
                        DateTimeOffset.FromUnixTimeSeconds(times[i]),
                        ("A", i, 10 * i, 1),
                        ("B", 7, 7, 0)
                    )
                );

            path = history.GetPath("server", Provider);
            Assert.False(File.Exists(path)); // nothing written per sample

            await history.PersistAsync();
            var firstWrite = File.ReadAllLines(path).Length;
            Assert.Equal(1 + 6, firstWrite); // one schema line, six rows

            history.Record(
                "server",
                Lag(DateTimeOffset.FromUnixTimeSeconds(times[3]), ("A", 3, 30, 1))
            );
            await history.StopAsync(default); // appends the last row
            Assert.Equal(firstWrite + 1, File.ReadAllLines(path).Length);
        }

        using (var restarted = Create(options))
        {
            await restarted.StartAsync(default);
            Assert.Equal(["server"], restarted.GetSources());
            Assert.Equal(
                ["A", "B"],
                restarted.GetLabels("server", Provider, Group).Select(label => label.Label)
            );
            Assert.Contains(
                restarted.GetFields("server"),
                field =>
                    field is { Field: "P99Ms", Unit: "ms", LabelDescription: "Player Steam ID" }
            );
            Assert.Equal(
                times.Select((time, i) => new HistorySample(time, 1, 10 * i)),
                Read(restarted, "server", "A", "P99Ms")
            );

            // The Analytics chart of one label reads the restored history.
            using var metrics = new MetricsStoreService(
                null!,
                options,
                NullLogger<MetricsStoreService>.Instance
            );
            var key = PluginStatsHistoryService.BuildMetricKey(Provider, Group, "P99Ms", "A");
            var chart = Assert.Single(
                new AnalyticsSeriesService(metrics, new(), new(), null, restarted)
                    .Build(times[0] - 60, times[3] + 60, ["server"], [key], 1000)
                    .Charts
            );
            Assert.Equal(key, chart.Metric);
            Assert.Equal(
                [0.0, 10, 20, 30],
                Assert.Single(chart.Series).Y.Where(y => y is not null).Select(y => y!.Value)
            );
            await restarted.StopAsync(default);
        }
    }

    [Fact]
    public void ClusterNodesShareOneHistoryPerLabel()
    {
        using var history = Create(new AnalyticsStoreOptions());
        using var metrics = new MetricsStoreService(
            null!,
            new AnalyticsStoreOptions(),
            NullLogger<MetricsStoreService>.Instance
        );
        var live = new PluginStatsStoreService(history);
        var registry = new AgentRegistry(null!, metrics, new ProfilerStoreService(), live);

        // The player is handed over from node-a (slot s1) to node-b (slot s2), then a node recycle.
        Publish(registry, "first", "s1", "node-a", 1, Now.AddSeconds(-30), 50);
        Publish(registry, "second", "s2", "node-b", 1, Now.AddSeconds(-20), 60);
        Publish(registry, "third", "s2", "node-c", 2, Now.AddSeconds(-10), 70);

        var clusterKey = PluginStatsHistoryService.ClusterKey("cluster-a");
        Assert.Equal([clusterKey], history.GetSources());
        var label = Assert.Single(history.GetLabels(clusterKey, Provider, Group));
        Assert.Equal(("76561198000000001", 3), (label.Label, label.Rows));
        Assert.Equal(
            [50.0, 60, 70],
            Read(history, clusterKey, "76561198000000001", "P99Ms").Select(s => s.Value)
        );

        // The live store still keeps one timeline per node process for the existing charts.
        var agents = registry.GetAgents();
        Assert.Equal(3, agents.Select(agent => agent.TelemetryKey).Distinct().Count());
        Assert.All(agents, agent => Assert.Equal(clusterKey, agent.PluginStatsKey));
        var timelines = live.Read(
            Provider,
            Now.AddMinutes(-1).ToUnixTimeSeconds(),
            Now.ToUnixTimeSeconds(),
            agents.Select(agent => agent.TelemetryKey).ToList()
        );
        Assert.Equal(3, timelines.Count);
    }

    [Fact]
    public void StandaloneServerHistoryIsKeyedByItsUniqueName()
    {
        var state = new AgentRuntimeState
        {
            Hello = new AgentHello { AgentId = "a", UniqueName = "standalone" },
        };
        Assert.Equal("standalone", state.PluginStatsKey);
    }

    [Fact]
    public async Task CompactionKeepsTheFileUnderTheCapByRollingUpOldDetailFirst()
    {
        const long cap = 64 * 1024;
        var options = new AnalyticsStoreOptions { PluginStatsMaxFileBytes = cap };
        string[] labels = ["A", "B", "C", "D", "E"];
        using (var history = Create(options))
        {
            // Six hours of reports every 10 s from five players: about 1.3 MB of raw rows.
            for (var t = Now.AddHours(-6); t < Now; t = t.AddSeconds(10))
                history.Record(
                    "server",
                    Lag(t, labels.Select(label => (label, 1.0, 2.0, 1.0)).ToArray())
                );

            await history.PersistAsync(); // a plain append, over the cap
            var path = history.GetPath("server", Provider);
            var appended = new FileInfo(path).Length;
            Assert.True(appended > cap);

            await history.PersistAsync(); // the overgrown file is compacted
            var compacted = new FileInfo(path).Length;
            output.WriteLine($"cap {cap} B: appended {appended} B, compacted {compacted} B");
            Assert.True(compacted <= cap, $"{compacted} bytes");
            await history.StopAsync(default);
        }

        using var restarted = Create(options);
        await restarted.StartAsync(default);
        var tiers = restarted.CountRows("server", Provider, Group, "A");
        output.WriteLine(
            $"label A after restart: raw {tiers[0]}, minute {tiers[1]}, hour {tiers[2]} rows"
        );
        Assert.True(tiers[0] > 0 && tiers[2] > 0, string.Join(",", tiers)); // newest still raw, oldest in hours

        // Nothing was dropped, only rolled up: the oldest hour is still there, and every hitch counted.
        var hitches = Read(restarted, "server", "A", "Hitches100");
        Assert.Equal(Now.AddHours(-6).ToUnixTimeSeconds(), hitches[0].Time);
        Assert.Equal(6 * 360, hitches.Sum(sample => sample.Value));
        await restarted.StopAsync(default);
    }

    private PluginStatsHistoryService Create(AnalyticsStoreOptions options) =>
        new(options, NullLogger<PluginStatsHistoryService>.Instance, new Clock(Now), _root);

    private static List<HistorySample> Read(
        PluginStatsHistoryService history,
        string source,
        string label,
        string field
    ) => [.. history.Read(source, Provider, Group, label, field, 0, long.MaxValue)?.Samples ?? []];

    private static PluginStatsProvider Lag(
        DateTimeOffset at,
        params (string Label, double P50, double P99, double Hitches)[] rows
    ) =>
        new()
        {
            Provider = Provider,
            CapturedAtUtc = at,
            Groups =
            [
                new PluginStatGroup
                {
                    Name = Group,
                    LabelDescription = "Player Steam ID",
                    Fields =
                    [
                        Field("P50Ms", "gauge", "Mean", "ms"),
                        Field("P99Ms", "gauge", "Max", "ms"),
                        Field("Hitches100", "counter", "Sum", ""),
                    ],
                    Instances =
                    [
                        .. rows.Select(row => new PluginStatInstance
                        {
                            Label = row.Label,
                            Values = [row.P50, row.P99, row.Hitches],
                        }),
                    ],
                },
            ],
        };

    private static PluginStatField Field(string name, string kind, string overTime, string unit) =>
        new()
        {
            Name = name,
            Kind = kind,
            Description = name,
            Unit = unit,
            OverTime = overTime,
        };

    private static void Publish(
        AgentRegistry registry,
        string agentId,
        string slot,
        string node,
        long epoch,
        DateTimeOffset at,
        double p99
    )
    {
        registry.UpsertHello(
            new AgentHello
            {
                AgentId = agentId,
                UniqueName = "slot-server",
                ClusterMode = true,
                ClusterId = "cluster-a",
                ClusterSlot = slot,
                ClusterNodeId = node,
                ClusterEpoch = epoch,
            },
            agentId,
            (_, _) => Task.CompletedTask
        );
        registry.UpdateSnapshot(
            new AgentSnapshot
            {
                AgentId = agentId,
                UniqueName = "slot-server",
                ClusterMode = true,
                ClusterId = "cluster-a",
                ClusterSlot = slot,
                ClusterNodeId = node,
                ClusterEpoch = epoch,
                IsRunning = true,
                CapturedAtUtc = at,
                PluginStats = new PluginStatsSnapshot
                {
                    Providers = [Lag(at, ("76561198000000001", 1, p99, 0))],
                },
            },
            agentId
        );
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
