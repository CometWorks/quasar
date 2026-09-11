using System.Text.Json;
using Magnetar.Protocol.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services.Analytics;
using Xunit;

namespace Quasar.Tests;

public sealed class AnalyticsCpuTests
{
    [Fact]
    public void SnapshotKeepsProcessAndSimulationCpuSeparate()
    {
        var sample = MetricSampleFactory.FromSnapshot(new AgentSnapshot
        {
            CapturedAtUtc = DateTimeOffset.FromUnixTimeSeconds(60),
            Metrics = new ServerMetrics { ServerCpuLoadPercent = 155, SimCpuLoadPercent = 25 },
        });

        Assert.Equal(155f, sample.CpuPercent);
        Assert.Equal(25f, sample.SimCpuPercent);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(3600)]
    public void RollupsExcludeMissingSimulationCpuAndRetainZeroAndOverBudgetValues(int interval)
    {
        var rollup = new RrdRollupBuffer(10, interval);
        rollup.Observe(Sample(0));
        rollup.Observe(Sample(interval, null)); // Flush a wholly missing window.
        rollup.Observe(Sample(interval + 1, 0));
        rollup.Observe(Sample(interval + 2, 150));
        rollup.Observe(Sample(interval * 2));
        rollup.Observe(Sample(interval * 3)); // Check accumulator reset.

        var samples = rollup.ReadAll();
        Assert.Equal(3, samples.Length);
        Assert.Null(samples[0].SimCpuPercent);
        Assert.Equal(75f, samples[1].SimCpuPercent);
        Assert.Null(samples[2].SimCpuPercent);
        Assert.All(samples, sample => Assert.Equal(155f, sample.CpuPercent));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0f)]
    [InlineData(25f)]
    [InlineData(150f)]
    public void PersistedCpuRoundTripsWithoutInventingMissingHistory(float? simCpu)
    {
        var line = MetricsStoreService.PersistedMetricLogLine.From("m", Sample(60, simCpu));
        var json = JsonSerializer.Serialize(line);
        var restored = JsonSerializer.Deserialize<MetricsStoreService.PersistedMetricLogLine>(json)!.ToMetricSample();

        Assert.Equal(155f, restored.CpuPercent);
        Assert.Equal(simCpu, restored.SimCpuPercent);

        // Older analytics.jsonl entries contain Cpu but no Scpu.
        var legacy = JsonSerializer.Deserialize<MetricsStoreService.PersistedMetricLogLine>(
            """{"b":"r","T":60,"Cpu":155,"Ss":1}""")!.ToMetricSample();
        Assert.Equal(155f, legacy.CpuPercent);
        Assert.Null(legacy.SimCpuPercent);
    }

    [Fact]
    public void ChartsKeepCpuSeriesSeparateAndShowGapsForMissingHistory()
    {
        // Populate the in-memory store without starting a hosted service or touching disk.
        using var store = new MetricsStoreService(null!, new AnalyticsStoreOptions(), NullLogger<MetricsStoreService>.Instance);
        store.Enqueue("test", Sample(60));
        var server = store.GetStore("test")!;
        server.Ingest(Sample(60));
        server.Ingest(Sample(61, 0));
        server.Ingest(Sample(62, 150));
        var service = new AnalyticsSeriesService(store, new ProfilerStoreService(), new PluginStatsStoreService());

        var charts = service.Build(60, 63, ["test"], ["cpu", "simcpu"], 1000).Charts;
        var process = Assert.Single(charts, chart => chart.Metric == "cpu");
        var simulation = Assert.Single(charts, chart => chart.Metric == "simcpu");

        Assert.Equal(new double?[] { 155, 155, 155 }, Assert.Single(process.Series).Y);
        Assert.Equal(new double?[] { null, 0, 150 }, Assert.Single(simulation.Series).Y);
        Assert.Equal(process.X, simulation.X);
        Assert.Null(process.Axis.Max);
        Assert.Null(simulation.Axis.Max);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidSimulationCpuBecomesMissing(float value)
    {
        Assert.Null(Sample(60, value).SimCpuPercent);
    }

    private static MetricSample Sample(long timestamp, float? simCpu = null) =>
        new(timestamp, 1, 155, 1024, 16.67f, 0, 0, 0, 0, simCpu);
}
